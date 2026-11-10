using System.Threading.RateLimiting;
using HotChocolate;

namespace GUtv_backend_dotnet.Services;

public enum RateLimitPolicy
{
    LoginFailuresPerAccountAndClient,
    LoginFailuresPerAccount,
    LoginFailuresPerClient,
    RegistrationPerClient,
    RefreshPerClient,
    AvatarUploadPerUser,
    TelegramLinkFailuresPerChat
}

// Counters live in this process: they reset on restart and are not shared between backend instances.
public sealed class RequestRateLimiter(TimeProvider? timeProvider = null) : IDisposable
{
    public const string ErrorCode = "RATE_LIMITED";
    private const int MaxKeyLength = 128;
    private const int SweepInterval = 256;

    // Client limits are per IP address. Students on a shared campus network share one address,
    // so those limits stay generous.
    //
    // Password guessing is stopped per account and address: ten wrong passwords lock only the
    // address that sent them, so one person cannot lock somebody else out of their account.
    // The per-account limit is the backstop against guessing from many addresses.
    private static readonly Dictionary<RateLimitPolicy, (int Permits, TimeSpan Window)> Limits = new()
    {
        [RateLimitPolicy.LoginFailuresPerAccountAndClient] = (10, TimeSpan.FromMinutes(15)),
        [RateLimitPolicy.LoginFailuresPerAccount] = (30, TimeSpan.FromMinutes(15)),
        [RateLimitPolicy.LoginFailuresPerClient] = (50, TimeSpan.FromMinutes(15)),
        [RateLimitPolicy.RegistrationPerClient] = (60, TimeSpan.FromHours(1)),
        // The site keeps the access token in memory, so every full page load refreshes it.
        // Refresh tokens are long random values; this limit only bounds the load per address.
        [RateLimitPolicy.RefreshPerClient] = (1200, TimeSpan.FromMinutes(5)),
        [RateLimitPolicy.AvatarUploadPerUser] = (20, TimeSpan.FromMinutes(10)),
        [RateLimitPolicy.TelegramLinkFailuresPerChat] = (5, TimeSpan.FromMinutes(15))
    };

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    private readonly PartitionedRateLimiter<(RateLimitPolicy Policy, string Key)> limiter =
        PartitionedRateLimiter.Create<(RateLimitPolicy Policy, string Key), (RateLimitPolicy Policy, string Key)>(
            resource => RateLimitPartition.GetFixedWindowLimiter(resource, partition => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Limits[partition.Policy].Permits,
                Window = Limits[partition.Policy].Window,
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    private sealed class FailureWindow
    {
        public int Failures;
        public int InFlight;
        public DateTimeOffset ExpiresAt;
    }

    // One short lock guards all failure counters: the work under it is a few integer updates.
    private readonly Lock failuresLock = new();
    private readonly Dictionary<(RateLimitPolicy Policy, string Key), FailureWindow> failures = new();
    private int reservationsSinceSweep;

    // Counts every attempt.
    public void Acquire(RateLimitPolicy policy, string key)
    {
        using var lease = limiter.AttemptAcquire((policy, Normalize(key)));
        if (!lease.IsAcquired)
            throw CreateException(lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : null);
    }

    // Counts only failed attempts: successful ones never lock anybody out.
    //
    // An attempt reserves a slot before it runs and the slot is released only on success, so
    // sending many attempts at once cannot get more of them past the limit than sending them
    // one by one.
    public async Task<T> LimitFailuresAsync<T>(RateLimitPolicy policy, string key, Func<Task<T>> operation)
    {
        var resource = (policy, Normalize(key));
        Reserve(resource);

        var failed = false;
        try
        {
            return await operation();
        }
        catch (GraphQLException exception) when (!IsRateLimited(exception))
        {
            failed = true;
            throw;
        }
        finally
        {
            Release(resource, failed);
        }
    }

    public static string ClientKey(HttpContext? context) =>
        context?.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static string AccountKey(string? login) => login?.Trim().ToLowerInvariant() ?? "";

    public static bool IsRateLimited(GraphQLException exception) =>
        exception.Errors.Any(error => error.Code == ErrorCode);

    public void Dispose() => limiter.Dispose();

    private void Reserve((RateLimitPolicy Policy, string Key) resource)
    {
        var (permits, _) = Limits[resource.Policy];
        lock (failuresLock)
        {
            var now = clock.GetUtcNow();
            if (++reservationsSinceSweep >= SweepInterval)
            {
                reservationsSinceSweep = 0;
                foreach (var stale in failures.Where(entry => IsIdle(entry.Value, now)).Select(entry => entry.Key).ToList())
                    failures.Remove(stale);
            }

            if (!failures.TryGetValue(resource, out var window))
                failures[resource] = window = new FailureWindow();
            else if (window.Failures > 0 && window.ExpiresAt <= now)
                window.Failures = 0;

            if (window.Failures + window.InFlight >= permits)
            {
                // Attempts that are still running may yet succeed; only recorded failures
                // tell how long the caller has to wait.
                TimeSpan? retryAfter = window.Failures > 0 ? window.ExpiresAt - now : null;
                throw CreateException(retryAfter);
            }

            window.InFlight++;
        }
    }

    private void Release((RateLimitPolicy Policy, string Key) resource, bool failed)
    {
        lock (failuresLock)
        {
            if (!failures.TryGetValue(resource, out var window)) return;

            window.InFlight--;
            if (!failed) return;

            var now = clock.GetUtcNow();
            // The window starts with its first failure.
            if (window.Failures == 0 || window.ExpiresAt <= now)
            {
                window.Failures = 0;
                window.ExpiresAt = now + Limits[resource.Policy].Window;
            }
            window.Failures++;
        }
    }

    private static bool IsIdle(FailureWindow window, DateTimeOffset now) =>
        window.InFlight == 0 && (window.Failures == 0 || window.ExpiresAt <= now);

    private static string Normalize(string key) => key.Length > MaxKeyLength ? key[..MaxKeyLength] : key;

    private static GraphQLException CreateException(TimeSpan? retryAfter)
    {
        var message = retryAfter is { } delay
            ? $"Слишком много попыток. Повторите через {Math.Max(1, (int)Math.Ceiling(delay.TotalMinutes))} мин."
            : "Слишком много попыток. Повторите позже.";
        return new GraphQLException(ErrorBuilder.New().SetMessage(message).SetCode(ErrorCode).Build());
    }
}
