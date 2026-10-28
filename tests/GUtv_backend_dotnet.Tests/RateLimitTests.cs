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
    public async Task RepeatedWrongPasswordsLockTheAccountButSuccessfulLoginsDoNot()
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
                mutation.Login(new("first", "wrong-password"), users, test.Service, Client($"10.0.1.{i}"), limiter));
            Assert.False(RequestRateLimiter.IsRateLimited(wrong));
        }

        // The account stays locked from any address, even with the right password and different login casing.
        var locked = await Assert.ThrowsAsync<GraphQLException>(() =>
            mutation.Login(new(" FIRST ", Password), users, test.Service, Client("10.0.2.1"), limiter));
        Assert.True(RequestRateLimiter.IsRateLimited(locked));

        // Other accounts are unaffected.
        await SetPasswordAsync(test, 2);
        await mutation.Login(new("second", Password), users, test.Service, Client("10.0.2.1"), limiter);
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

    private static HttpContextAccessor Client(string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return new HttpContextAccessor { HttpContext = context };
    }

    private static async Task SetPasswordAsync(SessionDatabase test, int userId)
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(Password, workFactor: 4);
        await test.Db.Users.Where(user => user.Id == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.PasswordHash, hash));
        test.Db.ChangeTracker.Clear();
    }
}
