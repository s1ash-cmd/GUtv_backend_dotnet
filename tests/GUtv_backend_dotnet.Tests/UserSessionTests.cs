using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GUtv_backend_dotnet.Tests;

public class UserSessionTests
{
    [Fact]
    public async Task IndependentDeviceLoginsAndRotationsDoNotReplaceOtherSessions()
    {
        await using var test = await SessionDatabase.OpenAsync();
        var desktop = await test.Service.CreateAsync(1, "desktop");
        var mobile = await test.Service.CreateAsync(1, "mobile");
        Assert.NotEqual(desktop.RefreshToken, mobile.RefreshToken);
        var desktopRotated = await test.Service.RotateAsync(desktop.RefreshToken);
        var mobileRotated = await test.Service.RotateAsync(mobile.RefreshToken);
        Assert.NotEqual(desktopRotated.RefreshToken, mobileRotated.RefreshToken);
        Assert.Equal(2, await test.Db.UserSessions.CountAsync());
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(desktop.AccessToken);
        Assert.Equal(SessionId(desktop), Guid.Parse(jwt.Claims.Single(c => c.Type == "sid").Value));
        Assert.Equal(SessionId(desktop), SessionId(desktopRotated));
    }

    [Fact]
    public async Task LogoutRevokesOnlyItsSessionAndImmediatelyRejectsItsAccessToken()
    {
        await using var test = await SessionDatabase.OpenAsync();
        var first = await test.Service.CreateAsync(1, "first");
        var other = await test.Service.CreateAsync(1, "other");
        Assert.True(await test.Service.RevokeAsync(1, SessionId(first)));
        Assert.False(await test.Service.RevokeAsync(1, SessionId(first)));
        await Assert.ThrowsAsync<GraphQLException>(() => test.Service.RotateAsync(first.RefreshToken));
        var context = PrincipalContext(first);
        await RejectingMiddleware().InvokeAsync(context, test.Db);
        Assert.Equal(401, context.Response.StatusCode);
        await test.Service.RotateAsync(other.RefreshToken);
    }

    [Fact]
    public async Task UserCannotListOrRevokeAnotherAccountsSession()
    {
        await using var test = await SessionDatabase.OpenAsync();
        var first = await test.Service.CreateAsync(1, "first");
        Assert.False(await test.Service.RevokeAsync(2, SessionId(first)));
        Assert.Empty(await test.Service.GetActiveAsync(2, SessionId(first)));
        var context = PrincipalContext(first, 2);
        await RejectingMiddleware().InvokeAsync(context, test.Db);
        Assert.Equal(401, context.Response.StatusCode);
        await test.Service.RotateAsync(first.RefreshToken);
    }

    [Fact]
    public async Task LogoutAllRevokesEverySessionAndNewLoginStillWorks()
    {
        await using var test = await SessionDatabase.OpenAsync();
        var first = await test.Service.CreateAsync(1, "first");
        var second = await test.Service.CreateAsync(1, "second");
        await test.Service.RevokeAllAsync(1);
        Assert.Empty(await test.Service.GetActiveAsync(1, SessionId(first)));
        await Assert.ThrowsAsync<GraphQLException>(() => test.Service.RotateAsync(first.RefreshToken));
        await Assert.ThrowsAsync<GraphQLException>(() => test.Service.RotateAsync(second.RefreshToken));
        var fresh = await test.Service.CreateAsync(1, "new-login");
        await test.Service.RotateAsync(fresh.RefreshToken);
        Assert.Single(await test.Service.GetActiveAsync(1, SessionId(fresh)));
    }

    [Fact]
    public async Task BanThenUnbanDoesNotRestoreOldSessions()
    {
        await using var test = await SessionDatabase.OpenAsync();
        var first = await test.Service.CreateAsync(1, "first");
        await new UserService(test.Db).SetBanned(1, true);
        await Assert.ThrowsAsync<GraphQLException>(() => test.Service.CreateAsync(1, "banned"));
        await new UserService(test.Db).SetBanned(1, false);
        await Assert.ThrowsAsync<GraphQLException>(() => test.Service.RotateAsync(first.RefreshToken));
        Assert.NotNull((await test.Db.UserSessions.AsNoTracking().SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task SessionListMarksCurrentAndHidesExpiredAndRevoked()
    {
        await using var test = await SessionDatabase.OpenAsync();
        var first = await test.Service.CreateAsync(1, "first");
        await test.Service.CreateAsync(1, "new-browser");
        var list = await test.Service.GetActiveAsync(1, SessionId(first));
        Assert.Equal(2, list.Count);
        Assert.Equal(SessionId(first), Assert.Single(list, s => s.IsCurrent).Id);
        await test.Service.RevokeAsync(1, SessionId(first));
        Assert.Equal("new-browser", Assert.Single(await test.Service.GetActiveAsync(1, Guid.Empty)).UserAgent);
        await test.Db.UserSessions.ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        Assert.Empty(await test.Service.GetActiveAsync(1, Guid.Empty));
    }

    [Fact]
    public async Task FailedLoginSessionCreationDoesNotPersistSessionOrRoleUpgrade()
    {
        await using var test = await SessionDatabase.OpenAsync();
        var user = await test.Db.Users.SingleAsync(u => u.Id == 1);
        user.JoinYear = DateTime.UtcNow.Year - 1;
        await test.Db.SaveChangesAsync();
        var service = new UserSessionService(test.Db, new UserService(test.Db), SessionDatabase.Auth("short"));
        await Assert.ThrowsAnyAsync<Exception>(() => service.CreateAsync(1, "first"));
        Assert.Empty(await test.Db.UserSessions.ToListAsync());
        Assert.Equal(UserRole.User, (await test.Db.Users.AsNoTracking().SingleAsync(u => u.Id == 1)).Role);
    }

    [Fact]
    public async Task UserAgentIsBoundedAndControlCharactersAreRemoved()
    {
        await using var test = await SessionDatabase.OpenAsync();
        await test.Service.CreateAsync(1, "browser\n\r\0" + new string('x', 1000));
        var agent = (await test.Service.GetActiveAsync(1, Guid.Empty)).Single().UserAgent!;
        Assert.Equal(500, agent.Length);
        Assert.DoesNotContain(agent, char.IsControl);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("banned")]
    [InlineData("missing-sid")]
    [InlineData("unknown-sid")]
    public async Task RequestValidationRejectsUnusableSessions(string reason)
    {
        await using var test = await SessionDatabase.OpenAsync();
        var first = await test.Service.CreateAsync(1, "first");
        if (reason == "expired") await test.Db.UserSessions.ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        if (reason == "banned") await test.Db.Users.ExecuteUpdateAsync(s => s.SetProperty(x => x.Banned, true));
        var context = PrincipalContext(first);
        var identity = (ClaimsIdentity)context.User.Identity!;
        if (reason == "missing-sid") identity.RemoveClaim(identity.FindFirst("sid")!);
        if (reason == "unknown-sid") { identity.RemoveClaim(identity.FindFirst("sid")!); identity.AddClaim(new Claim("sid", Guid.NewGuid().ToString())); }
        await RejectingMiddleware().InvokeAsync(context, test.Db);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown-token")]
    public async Task InvalidTokenDoesNotCreateSessions(string token)
    {
        await using var test = await SessionDatabase.OpenAsync();
        await Assert.ThrowsAsync<GraphQLException>(() => test.Service.RotateAsync(token));
        Assert.Empty(await test.Db.UserSessions.ToListAsync());
    }

    [Fact]
    public async Task ValidSessionUsesCurrentRoleAndAnonymousRequestsRemainPublic()
    {
        await using var test = await SessionDatabase.OpenAsync();
        var first = await test.Service.CreateAsync(1, "first");
        var context = PrincipalContext(first);
        var reached = 0;
        var middleware = new SessionAuthenticationMiddleware(_ => { reached++; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, test.Db);
        Assert.True(context.User.IsInRole("User"));
        Assert.False(context.User.IsInRole("Admin"));
        await middleware.InvokeAsync(new DefaultHttpContext(), test.Db);
        Assert.Equal(2, reached);
    }

    private static Guid SessionId(RefreshedSession session) => Guid.Parse(
        new JwtSecurityTokenHandler().ReadJwtToken(session.AccessToken).Claims.Single(c => c.Type == "sid").Value);
    private static DefaultHttpContext PrincipalContext(RefreshedSession session, int userId = 1) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim("sid", SessionId(session).ToString()),
            new Claim(ClaimTypes.Role, "Admin")], "Bearer"))
    };
    private static SessionAuthenticationMiddleware RejectingMiddleware() => new(_ => throw new Exception("Must reject"));
}

internal sealed class SessionDatabase(SqliteConnection connection, AppDbContext db) : IAsyncDisposable
{
    public AppDbContext Db => db;
    public UserSessionService Service => new(db, new UserService(db), Auth());
    public static AuthService Auth(string key = "regression-test-key-at-least-32-bytes-long") => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = key, ["Jwt:Issuer"] = "regression-tests", ["Jwt:Audience"] = "regression-tests"
        }).Build());
    public static async Task<SessionDatabase> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new TestDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Users.AddRange(new User { Id = 1, Login = "first", Name = "First", JoinYear = DateTime.UtcNow.Year },
            new User { Id = 2, Login = "second", Name = "Second", JoinYear = DateTime.UtcNow.Year });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new SessionDatabase(connection, db);
    }
    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }
    private sealed class TestDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<User>().Property(u => u.NormalizedLogin).HasComputedColumnSql("lower(trim(\"Login\"))", stored: true);
        }
    }
}
