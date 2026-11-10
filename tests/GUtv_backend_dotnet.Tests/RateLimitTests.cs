using System.Net;
using GUtv_backend_dotnet.GraphQL.Mutations;
using GUtv_backend_dotnet.Services;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.Tests;

public class RateLimitTests
{
    private const string Password = "test-password";

    [Fact]
    public async Task WrongPasswordsLockOnlyTheAddressThatSentThemAndSuccessfulLoginsAreNotCounted()
    {
        await using var test = await SessionDatabase.OpenAsync();
        await SetPasswordAsync(test, 1);
        using var limiter = new RequestRateLimiter();
        var users = new UserService(test.Db);
        var mutation = new UserMutation();

        // Successful logins are not counted.
        for (var i = 0; i < 12; i++)
            await mutation.Login(new("first", Password), users, test.Service, Client("10.0.0.1"), limiter);

        for (var i = 0; i < 10; i++)
        {
            var wrong = await Assert.ThrowsAsync<GraphQLException>(() =>
                mutation.Login(new("first", "wrong-password"), users, test.Service, Client("10.0.0.5"), limiter));
            Assert.False(RequestRateLimiter.IsRateLimited(wrong));
        }

        // That address is now refused for this account, even with the right password and different login casing.
        var locked = await Assert.ThrowsAsync<GraphQLException>(() =>
            mutation.Login(new(" FIRST ", Password), users, test.Service, Client("10.0.0.5"), limiter));
        Assert.True(RequestRateLimiter.IsRateLimited(locked));

        // The owner of the account, coming from elsewhere, is not locked out by somebody else's guesses.
        await mutation.Login(new("first", Password), users, test.Service, Client("10.0.2.1"), limiter);

        // Other accounts are unaffected, also from the address that was guessing.
        await SetPasswordAsync(test, 2);
        await mutation.Login(new("second", Password), users, test.Service, Client("10.0.0.5"), limiter);
    }

    [Fact]
    public async Task GuessingFromManyAddressesLocksTheAccountFromEveryAddress()
    {
        await using var test = await SessionDatabase.OpenAsync();
        await SetPasswordAsync(test, 1);
        await SetPasswordAsync(test, 2);
        using var limiter = new RequestRateLimiter();
        var users = new UserService(test.Db);
        var mutation = new UserMutation();

        for (var i = 0; i < 30; i++)
        {
            var wrong = await Assert.ThrowsAsync<GraphQLException>(() =>
                mutation.Login(new("first", "wrong-password"), users, test.Service, Client($"10.0.1.{i}"), limiter));
            Assert.False(RequestRateLimiter.IsRateLimited(wrong));
        }

        var locked = await Assert.ThrowsAsync<GraphQLException>(() =>
            mutation.Login(new("first", Password), users, test.Service, Client("10.0.2.1"), limiter));
        Assert.True(RequestRateLimiter.IsRateLimited(locked));
        await mutation.Login(new("second", Password), users, test.Service, Client("10.0.2.1"), limiter);
    }

    [Fact]
    public async Task AttemptsSentAtOnceCannotGetMoreGuessesThanAttemptsSentOneByOne()
    {
        using var limiter = new RequestRateLimiter();
        var started = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, 40).Select(_ => limiter.LimitFailuresAsync<int>(
            RateLimitPolicy.TelegramLinkFailuresPerChat, "900", async () =>
            {
                Interlocked.Increment(ref started);
                await release.Task;
                throw new GraphQLException("wrong code");
            })).ToList();

        // Only as many guesses as the limit allows are ever checked; the rest are refused outright.
        Assert.Equal(5, started);
        release.SetResult();
        var outcomes = new List<GraphQLException>();
        foreach (var attempt in attempts)
            outcomes.Add(await Assert.ThrowsAsync<GraphQLException>(() => attempt));
        Assert.Equal(5, outcomes.Count(error => !RequestRateLimiter.IsRateLimited(error)));
        Assert.Equal(35, outcomes.Count(RequestRateLimiter.IsRateLimited));
        Assert.Equal(5, started);
    }

    [Fact]
    public async Task FailuresExpireWithTheirWindowAndASuccessfulAttemptKeepsNoSlot()
    {
        var clock = new ManualClock();
        using var limiter = new RequestRateLimiter(clock);
        Task<int> Attempt(bool succeed) => limiter.LimitFailuresAsync(
            RateLimitPolicy.TelegramLinkFailuresPerChat, "900",
            () => succeed ? Task.FromResult(1) : throw new GraphQLException("wrong code"));

        for (var i = 0; i < 20; i++) await Attempt(succeed: true);
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<GraphQLException>(() => Attempt(succeed: false));

        var limited = await Assert.ThrowsAsync<GraphQLException>(() => Attempt(succeed: true));
        Assert.True(RequestRateLimiter.IsRateLimited(limited));
        Assert.Contains("через 15 мин", Assert.Single(limited.Errors).Message);

        clock.Now += TimeSpan.FromMinutes(14);
        Assert.True(RequestRateLimiter.IsRateLimited(
            await Assert.ThrowsAsync<GraphQLException>(() => Attempt(succeed: true))));

        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal(1, await Attempt(succeed: true));
    }

    [Fact]
    public async Task SignInKeepsTheRefreshTokenInAnHttpOnlyCookieAndOutOfTheResponse()
    {
        await using var test = await SessionDatabase.OpenAsync();
        await SetPasswordAsync(test, 1);
        using var limiter = new RequestRateLimiter();
        var users = new UserService(test.Db);
        var mutation = new UserMutation();

        var login = Client("10.0.0.1");
        var signedIn = await mutation.Login(new("first", Password), users, test.Service, login, limiter);
        Assert.Null(signedIn.RefreshToken);
        var cookie = Assert.Single(login.HttpContext!.Response.Headers.SetCookie)!;
        Assert.StartsWith(RefreshTokenCookie.Name + "=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/graphql", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);

        // The cookie alone refreshes the session, and the rotated token again stays in a cookie.
        var refresh = Client("10.0.0.1", cookie.Split(';')[0]);
        var refreshed = await mutation.RefreshToken(test.Service, refresh, limiter);
        Assert.Null(refreshed.RefreshToken);
        Assert.NotEmpty(refreshed.AccessToken);
        var rotatedCookie = Assert.Single(refresh.HttpContext!.Response.Headers.SetCookie)!;
        Assert.NotEqual(cookie.Split(';')[0], rotatedCookie.Split(';')[0]);

        // The cookie that was just replaced no longer works, and its rejection leaves the browser's cookie alone.
        var stale = Client("10.0.0.1", cookie.Split(';')[0]);
        await Assert.ThrowsAsync<GraphQLException>(() => mutation.RefreshToken(test.Service, stale, limiter));
        Assert.Equal(0, stale.HttpContext!.Response.Headers.SetCookie.Count);

        // Without a cookie or an argument there is nothing to refresh.
        var missing = await Assert.ThrowsAsync<GraphQLException>(() =>
            mutation.RefreshToken(test.Service, Client("10.0.0.1"), limiter));
        Assert.Equal("Недействительный refresh token", Assert.Single(missing.Errors).Message);
    }

    [Fact]
    public async Task PageThatStillHoldsItsOwnRefreshTokenIsMovedToTheCookie()
    {
        await using var test = await SessionDatabase.OpenAsync();
        using var limiter = new RequestRateLimiter();
        var mutation = new UserMutation();
        var session = await test.Service.CreateAsync(1, "legacy-page");

        var request = Client("10.0.0.1");
        var refreshed = await mutation.RefreshToken(test.Service, request, limiter, session.RefreshToken);

        // Such a page keeps working until it reloads, so it still receives the rotated token.
        Assert.NotNull(refreshed.RefreshToken);
        Assert.NotEqual(session.RefreshToken, refreshed.RefreshToken);
        var cookie = Assert.Single(request.HttpContext!.Response.Headers.SetCookie)!;
        Assert.StartsWith($"{RefreshTokenCookie.Name}={Uri.EscapeDataString(refreshed.RefreshToken!)}", cookie);
    }

    [Fact]
    public void SecureCookieFollowsTheRequestSchemeAndClearingMatchesItsAttributes()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        RefreshTokenCookie.Set(context, "token", DateTime.UtcNow.AddDays(7));
        Assert.Contains("secure", Assert.Single(context.Response.Headers.SetCookie)!, StringComparison.OrdinalIgnoreCase);

        var cleared = new DefaultHttpContext();
        RefreshTokenCookie.Clear(cleared);
        var removal = Assert.Single(cleared.Response.Headers.SetCookie)!;
        Assert.StartsWith(RefreshTokenCookie.Name + "=;", removal);
        Assert.Contains("path=/graphql", removal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=Thu, 01 Jan 1970", removal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OneAddressCannotSprayPasswordsAcrossManyAccounts()
    {
        await using var test = await SessionDatabase.OpenAsync();
        await SetPasswordAsync(test, 2);
        using var limiter = new RequestRateLimiter();
        var users = new UserService(test.Db);
        var mutation = new UserMutation();

        for (var i = 0; i < 50; i++)
            await Assert.ThrowsAsync<GraphQLException>(() =>
                mutation.Login(new($"missing-{i}", Password), users, test.Service, Client("10.0.0.9"), limiter));

        var locked = await Assert.ThrowsAsync<GraphQLException>(() =>
            mutation.Login(new("second", Password), users, test.Service, Client("10.0.0.9"), limiter));
        Assert.True(RequestRateLimiter.IsRateLimited(locked));
        await mutation.Login(new("second", Password), users, test.Service, Client("10.0.0.10"), limiter);
    }

    [Fact]
    public void AcquireRejectsRequestsAboveTheLimit()
    {
        using var limiter = new RequestRateLimiter();
        for (var i = 0; i < 20; i++)
            limiter.Acquire(RateLimitPolicy.AvatarUploadPerUser, "1");

        var error = Assert.Throws<GraphQLException>(() => limiter.Acquire(RateLimitPolicy.AvatarUploadPerUser, "1"));
        Assert.True(RequestRateLimiter.IsRateLimited(error));
        limiter.Acquire(RateLimitPolicy.AvatarUploadPerUser, "2");
    }

    [Fact]
    public async Task TelegramLinkCodeIsSingleUseAndExpires()
    {
        await using var test = await SessionDatabase.OpenAsync();
        var users = new UserService(test.Db);

        var code = await users.GenerateTelegramLinkCode(1);
        var linked = await users.LinkTelegramByCode(code, 501, "first_tg");
        Assert.Equal(1, linked.Id);
        Assert.Null(linked.TelegramLinkCode);
        await Assert.ThrowsAsync<GraphQLException>(() => users.LinkTelegramByCode(code, 502, "other"));

        var expired = await users.GenerateTelegramLinkCode(2);
        await test.Db.Users.Where(user => user.Id == 2)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.TelegramLinkCodeExpiry, DateTime.UtcNow.AddSeconds(-1)));
        test.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<GraphQLException>(() => users.LinkTelegramByCode(expired, 502, "second_tg"));
        Assert.Null((await test.Db.Users.AsNoTracking().SingleAsync(user => user.Id == 2)).TelegramChatId);
    }

    [Fact]
    public async Task ExpiredCodeOfAnotherAccountDoesNotCaptureTheLink()
    {
        await using var test = await SessionDatabase.OpenAsync();
        var users = new UserService(test.Db);
        var code = await users.GenerateTelegramLinkCode(2);
        // Simulate a stale, expired copy of the same code left on another account.
        await test.Db.Users.Where(user => user.Id == 1).ExecuteUpdateAsync(setters => setters
            .SetProperty(user => user.TelegramLinkCode, code)
            .SetProperty(user => user.TelegramLinkCodeExpiry, DateTime.UtcNow.AddMinutes(-5)));
        test.Db.ChangeTracker.Clear();

        var linked = await users.LinkTelegramByCode(code, 777, "second_tg");
        Assert.Equal(2, linked.Id);
    }

    [Fact]
    public async Task TelegramLinkGuessingIsLimitedPerChat()
    {
        await using var test = await SessionDatabase.OpenAsync();
        var users = new UserService(test.Db);
        using var limiter = new RequestRateLimiter();
        var code = await users.GenerateTelegramLinkCode(1);
        var wrong = code == "100000" ? "100001" : "100000";

        Task<Models.User> Link(string value, long chat) => limiter.LimitFailuresAsync(
            RateLimitPolicy.TelegramLinkFailuresPerChat, chat.ToString(), () => users.LinkTelegramByCode(value, chat, null));

        for (var i = 0; i < 5; i++)
            Assert.False(RequestRateLimiter.IsRateLimited(
                await Assert.ThrowsAsync<GraphQLException>(() => Link(wrong, 900))));

        // Even the right code is refused from a chat that exhausted its attempts.
        Assert.True(RequestRateLimiter.IsRateLimited(
            await Assert.ThrowsAsync<GraphQLException>(() => Link(code, 900))));
        Assert.Equal(1, (await Link(code, 901)).Id);
    }

    private static HttpContextAccessor Client(string address, string? cookie = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        if (cookie is not null) context.Request.Headers.Cookie = cookie;
        return new HttpContextAccessor { HttpContext = context };
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2030, 1, 10, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task SetPasswordAsync(SessionDatabase test, int userId)
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(Password, workFactor: 4);
        await test.Db.Users.Where(user => user.Id == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.PasswordHash, hash));
        test.Db.ChangeTracker.Clear();
    }
}
