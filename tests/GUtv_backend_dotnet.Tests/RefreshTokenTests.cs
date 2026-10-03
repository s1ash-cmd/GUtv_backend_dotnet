using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace GUtv_backend_dotnet.Tests;

public class RefreshTokenTests
{
    [Fact]
    public async Task RotationPersistsNewTokensAndRejectsReplay()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = await CreateContextAsync(connection);
        var service = new UserSessionService(db, new UserService(db), CreateAuthService());

        var session = await service.RotateAsync("original-token");

        Assert.NotEqual("original-token", session.RefreshToken);
        var persisted = await db.UserSessions.AsNoTracking().SingleAsync();
        Assert.Equal(UserSessionService.HashRefreshToken(session.RefreshToken), persisted.RefreshTokenHash);
        Assert.NotEqual(session.RefreshToken, persisted.RefreshTokenHash);
        Assert.True(persisted.ExpiresAt > DateTime.UtcNow.AddDays(6));
        Assert.Contains(new JwtSecurityTokenHandler().ReadJwtToken(session.AccessToken).Claims,
            claim => claim.Type == ClaimTypes.Role && claim.Value == "Osnova");
        await Assert.ThrowsAsync<GraphQLException>(() =>
            service.RotateAsync("original-token"));
        Assert.Equal(UserSessionService.HashRefreshToken(session.RefreshToken), (await db.UserSessions.AsNoTracking().SingleAsync()).RefreshTokenHash);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task BannedOrExpiredSessionCannotRotate(bool banned, bool expired)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = await CreateContextAsync(connection);
        var user = await db.Users.SingleAsync();
        user.Banned = banned;
        if (expired) (await db.UserSessions.SingleAsync()).ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<GraphQLException>(() =>
            new UserSessionService(db, new UserService(db), CreateAuthService()).RotateAsync("original-token"));

        Assert.Equal(UserSessionService.HashRefreshToken("original-token"), (await db.UserSessions.AsNoTracking().SingleAsync()).RefreshTokenHash);
    }

    [Fact]
    public async Task TokenChangedAfterLookupCannotBeConsumedAgain()
    {
        await using var connection = await OpenDatabaseAsync();
        var race = new TokenConsumptionInterceptor();
        await using var db = await CreateContextAsync(connection, race);

        await Assert.ThrowsAsync<GraphQLException>(() =>
            new UserSessionService(db, new UserService(db), CreateAuthService()).RotateAsync("original-token"));

        Assert.True(race.Intercepted);
        // The simulated competing update uses this test's transaction; rejection
        // rolls that transaction back instead of committing any stale-token change.
        Assert.Equal(UserSessionService.HashRefreshToken("original-token"), (await db.UserSessions.AsNoTracking().SingleAsync()).RefreshTokenHash);
    }

    [Fact]
    public async Task FailureToIssueAccessTokenRollsBackRefreshConsumptionAndRoleUpgrade()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = await CreateContextAsync(connection);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new UserSessionService(db, new UserService(db), CreateAuthService("short")).RotateAsync("original-token"));

        var persisted = await db.Users.AsNoTracking().SingleAsync();
        Assert.Equal(UserSessionService.HashRefreshToken("original-token"), (await db.UserSessions.AsNoTracking().SingleAsync()).RefreshTokenHash);
        Assert.Equal(UserRole.User, persisted.Role);
    }

    private static AuthService CreateAuthService(string key = "regression-test-key-at-least-32-bytes-long") => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = key, ["Jwt:Issuer"] = "regression-tests", ["Jwt:Audience"] = "regression-tests"
        }).Build());

    private static async Task<SqliteConnection> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<AppDbContext> CreateContextAsync(SqliteConnection connection,
        DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        var db = new RefreshTestDbContext(options.Options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User
        {
            Id = 1, Login = "test-user", Name = "Test user", Role = UserRole.User,
            JoinYear = DateTime.UtcNow.Year - 1
        });
        db.UserSessions.Add(new UserSession { Id = Guid.NewGuid(), UserId = 1,
            RefreshTokenHash = UserSessionService.HashRefreshToken("original-token"),
            CreatedAt = DateTime.UtcNow, LastUsedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }

    private sealed class RefreshTestDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // Login normalization is PostgreSQL-specific and unrelated to rotation.
            modelBuilder.Entity<User>().Property(user => user.NormalizedLogin)
                .HasComputedColumnSql(null).ValueGeneratedNever();
        }
    }

    private sealed class TokenConsumptionInterceptor : DbCommandInterceptor
    {
        public bool Intercepted { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Intercepted && command.CommandText.StartsWith("UPDATE \"UserSessions\""))
            {
                Intercepted = true;
                await using var competitor = command.Connection!.CreateCommand();
                competitor.Transaction = command.Transaction;
                competitor.CommandText = "UPDATE \"UserSessions\" SET \"RefreshTokenHash\" = 'already-consumed'";
                await competitor.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
        }
    }
}
