using System.Threading.RateLimiting;
using HotChocolate;

namespace GUtv_backend_dotnet.Services;

public enum RateLimitPolicy
{
    LoginFailuresPerAccount,
    LoginFailuresPerClient,
    RegistrationPerClient,
    RefreshPerClient,
    AvatarUploadPerUser,
    TelegramLinkFailuresPerChat
}

// Counters live in this process: they reset on restart and are not shared between backend instances.
public sealed class RequestRateLimiter : IDisposable
{
    public const string ErrorCode = "RATE_LIMITED";
    private const int MaxKeyLength = 128;

    // Client limits are per IP address. Students on a shared campus network share one address,
    // so those limits stay generous; per-account limits catch password guessing.
    private static readonly Dictionary<RateLimitPolicy, (int Permits, TimeSpan Window)> Limits = new()
    {
        [RateLimitPolicy.LoginFailuresPerAccount] = (10, TimeSpan.FromMinutes(15)),
        [RateLimitPolicy.LoginFailuresPerClient] = (50, TimeSpan.FromMinutes(15)),
        [RateLimitPolicy.RegistrationPerClient] = (20, TimeSpan.FromHours(1)),
        [RateLimitPolicy.RefreshPerClient] = (300, TimeSpan.FromMinutes(5)),
        [RateLimitPolicy.AvatarUploadPerUser] = (20, TimeSpan.FromMinutes(10)),
        [RateLimitPolicy.TelegramLinkFailuresPerChat] = (5, TimeSpan.FromMinutes(15))
    };

    private readonly PartitionedRateLimiter<(RateLimitPolicy Policy, string Key)> limiter =
        PartitionedRateLimiter.Create<(RateLimitPolicy Policy, string Key), (RateLimitPolicy Policy, string Key)>(
            resource => RateLimitPartition.GetFixedWindowLimiter(resource, partition => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Limits[partition.Policy].Permits,
                Window = Limits[partition.Policy].Window,
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    // Counts every attempt.
    public void Acquire(RateLimitPolicy policy, string key)
    {
        using var lease = limiter.AttemptAcquire((policy, Normalize(key)));
        if (!lease.IsAcquired)
            throw CreateException(lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : null);
    }

    // Counts only failed attempts: successful ones never lock anybody out.
    public async Task<T> LimitFailuresAsync<T>(RateLimitPolicy policy, string key, Func<Task<T>> operation)
    {
        var resource = (policy, Normalize(key));
        if (limiter.GetStatistics(resource)?.CurrentAvailablePermits <= 0)
            throw CreateException(null);

        try
        {
            return await operation();
        }
        catch (GraphQLException exception) when (!IsRateLimited(exception))
        {
            limiter.AttemptAcquire(resource).Dispose();
            throw;
        }
    }

    public static string ClientKey(HttpContext? context) =>
        context?.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static string AccountKey(string? login) => login?.Trim().ToLowerInvariant() ?? "";

    public static bool IsRateLimited(GraphQLException exception) =>
        exception.Errors.Any(error => error.Code == ErrorCode);

    public void Dispose() => limiter.Dispose();

    private static string Normalize(string key) => key.Length > MaxKeyLength ? key[..MaxKeyLength] : key;

    private static GraphQLException CreateException(TimeSpan? retryAfter)
    {
        var message = retryAfter is { } delay
            ? $"Слишком много попыток. Повторите через {Math.Max(1, (int)Math.Ceiling(delay.TotalMinutes))} мин."
            : "Слишком много попыток. Повторите позже.";
        return new GraphQLException(ErrorBuilder.New().SetMessage(message).SetCode(ErrorCode).Build());
    }
}
