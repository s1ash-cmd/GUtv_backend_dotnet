using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.GraphQL.Mutations;
using GUtv_backend_dotnet.GraphQL.Queries;
using GUtv_backend_dotnet.GraphQL.Types;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace GUtv_backend_dotnet.Tests;

// Every test owns a newly created database. Never use a production connection.
public class PostgresSessionTests : IAsyncLifetime
{
    private string? databaseName;
    private string connectionString = "";
    private readonly string avatarDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gutv-session-avatars-" + Guid.NewGuid().ToString("N"));
    private WebApplication? app;
    private HttpClient client = null!;
    private const string Password = "test-password-123";
    private const string Key = "postgres-integration-test-key-at-least-32-bytes";
    private const string PreviousMigration = "20261003172311_AddBookingRevision";
    private DbContextOptions<AppDbContext> Options => new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;

    public async Task InitializeAsync()
    {
        var source = Environment.GetEnvironmentVariable("GUTV_SESSION_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(source)) return;
        databaseName = "gutv_session_test_" + Guid.NewGuid().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(source) { Database = "postgres" };
        await using (var admin = new NpgsqlConnection(builder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await command.ExecuteNonQueryAsync();
        }
        builder.Database = databaseName;
        connectionString = builder.ConnectionString;
        await using (var db = new AppDbContext(Options))
        {
            // Upgrade a database with users and legacy credentials, rather than
            // merely checking the SQL generated for an empty database.
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Users" ("Login", "PasswordHash", "Name", "Role", "Banned", "JoinYear", "RefreshToken", "RefreshTokenExpiryTime")
                VALUES ({"first"}, {BCrypt.Net.BCrypt.HashPassword(Password, 4)}, {"First"}, {0}, {false}, {DateTime.UtcNow.Year}, {"legacy-refresh"}, {DateTime.UtcNow.AddDays(1)})
                """);
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal("First", (await db.Users.SingleAsync()).Name);
            db.Users.Add(new User { Login = "second", Name = "Second", PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, 4), JoinYear = DateTime.UtcNow.Year });
            await db.SaveChangesAsync();
        }

        var host = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        host.Logging.ClearProviders();
        host.WebHost.UseUrls("http://127.0.0.1:0");
        host.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = Key, ["Jwt:Issuer"] = "session-tests", ["Jwt:Audience"] = "session-tests",
            ["AvatarStorage:Path"] = avatarDirectory
        });
        host.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
        host.Services.AddScoped<UserService>();
        host.Services.AddSingleton<AvatarImageStore>();
        host.Services.AddScoped<UserAvatarService>();
        host.Services.AddScoped<UserSessionService>();
        host.Services.AddScoped<AuthService>();
        host.Services.AddScoped<EquipmentService>();
        host.Services.AddScoped<BotSecurityService>();
        host.Services.AddHttpContextAccessor();
        host.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
                ValidIssuer = "session-tests", ValidAudience = "session-tests",
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key))
            };
        });
        host.Services.AddAuthorization();
        host.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins("http://localhost:3107", "http://127.0.0.1:3107").AllowAnyHeader().AllowAnyMethod()));
        host.Services.AddGraphQLServer().AddQueryType<Query>().AddMutationType<Mutation>()
            .AddTypeExtension<UserQueries>().AddTypeExtension<UserMutation>()
            .AddProjections().AddFiltering().AddSorting().AddAuthorization()
            .AddType<UserRoleType>().AddType<EqCategoryType>().AddType<EqAccessType>().AddType<BookingStatusType>();
        app = host.Build();
        app.UseCors();
        app.UseAuthentication();
        app.UseMiddleware<SessionAuthenticationMiddleware>();
        app.UseAuthorization();
        app.MapGraphQL();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        client = new HttpClient { BaseAddress = new Uri(address) };
    }

    [PostgresFact]
    public async Task HttpLoginsRefreshIndependentlyAndLogoutImmediatelyRevokesOnlyOne()
    {
        var desktop = await LoginAsync("first");
        var mobile = await LoginAsync("first");
        var desktopNew = await RefreshAsync(desktop.RefreshToken);
        var mobileNew = await RefreshAsync(mobile.RefreshToken);
        var sessions = await QueryAsync("{ mySessions { id isCurrent userAgent createdAt lastUsedAt expiresAt } }", desktopNew.AccessToken);
        Assert.Equal(2, sessions.GetProperty("mySessions").GetArrayLength());
        Assert.Single(sessions.GetProperty("mySessions").EnumerateArray(), s => s.GetProperty("isCurrent").GetBoolean());
        await QueryAsync("mutation { logout }", desktopNew.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("{ me { id } }", desktopNew.AccessToken)).StatusCode);
        Assert.True((await SendAsync("{ me { id } }", mobileNew.AccessToken)).IsSuccessStatusCode);
        await AssertRefreshRejectedAsync(desktopNew.RefreshToken);
        await RefreshAsync(mobileNew.RefreshToken);
        await AssertRefreshRejectedAsync("legacy-refresh");
    }

    [PostgresFact]
    public async Task HttpAvatarUploadIsOwnedAndConcurrentReplacementsLeaveOnlyTheCurrentFile()
    {
        var first = await LoginAsync("first");
        var second = await LoginAsync("second");
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(80, 60);
        using var bytes = new MemoryStream();
        await image.SaveAsync(bytes, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        var variables = new { photo = Convert.ToBase64String(bytes.ToArray()) };
        const string upload = "mutation($photo: String!) { uploadMyAvatar(imageBase64: $photo) { id avatarUrl avatarSeed } }";

        using var anonymous = await SendAsync(upload, variables: variables);
        using var rejected = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync());
        Assert.Equal("AUTH_NOT_AUTHENTICATED", rejected.RootElement.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());

        await QueryAsync(upload, first.AccessToken, variables);
        await Task.WhenAll(QueryAsync(upload, first.AccessToken, variables), QueryAsync(upload, first.AccessToken, variables));
        var own = (await QueryAsync("{ me { avatarUrl } }", first.AccessToken)).GetProperty("me").GetProperty("avatarUrl").GetString()!;
        var other = (await QueryAsync("{ me { avatarUrl } }", second.AccessToken)).GetProperty("me").GetProperty("avatarUrl");
        Assert.Equal(JsonValueKind.Null, other.ValueKind);
        Assert.Single(Directory.GetFiles(avatarDirectory));
        Assert.True(File.Exists(System.IO.Path.Combine(avatarDirectory, own["/avatars/".Length..])));

        await QueryAsync("mutation { removeMyAvatar { avatarUrl } }", first.AccessToken);
        await QueryAsync("mutation { removeMyAvatar { avatarUrl } }", first.AccessToken);
        Assert.Empty(Directory.GetFiles(avatarDirectory));
    }

    [PostgresFact]
    public async Task HttpUserCannotRevokeOtherAccountAndLogoutAllRequiresAuthentication()
    {
        var first = await LoginAsync("first");
        var second = await LoginAsync("second");
        var list = await QueryAsync("{ mySessions { id } }", first.AccessToken);
        var id = list.GetProperty("mySessions")[0].GetProperty("id").GetString();
        var result = await QueryAsync("mutation($id: UUID!) { revokeMySession(sessionId: $id) }", second.AccessToken, new { id });
        Assert.False(result.GetProperty("revokeMySession").GetBoolean());
        await QueryAsync("{ me { id } }", first.AccessToken);
        await QueryAsync("{ me { id } }", second.AccessToken);
        var anonymous = await SendAsync("mutation { logoutAll }");
        var payload = await anonymous.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0);
        await QueryAsync("mutation { logoutAll }", first.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("{ me { id } }", first.AccessToken)).StatusCode);
        await QueryAsync("{ me { id } }", second.AccessToken);
    }

    [PostgresFact]
    public async Task HttpLogoutAllRevokesBothDevicesAndPrivateFieldsAreNotInSchema()
    {
        var first = await LoginAsync("first");
        var second = await LoginAsync("first");
        var invalid = await SendAsync("{ mySessions { refreshTokenHash userId user { passwordHash } } }", first.AccessToken);
        var invalidPayload = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(invalidPayload.TryGetProperty("errors", out _));
        await QueryAsync("mutation { logoutAll }", second.AccessToken);
        foreach (var session in new[] { first, second })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("{ me { id } }", session.AccessToken)).StatusCode);
            await AssertRefreshRejectedAsync(session.RefreshToken);
        }
        var newLogin = await LoginAsync("first");
        await QueryAsync("{ me { id } }", newLogin.AccessToken);
    }

    [PostgresFact]
    public async Task ConcurrentRefreshConsumesOneTokenExactlyOnce()
    {
        var first = await LoginAsync("first");
        await using var db1 = new AppDbContext(Options);
        await using var db2 = new AppDbContext(Options);
        var attempts = await Task.WhenAll(TryRotateAsync(db1, first.RefreshToken), TryRotateAsync(db2, first.RefreshToken));
        var winner = Assert.Single(attempts, s => s is not null)!;
        Assert.Single(attempts, s => s is null);
        await QueryAsync("{ me { id } }", winner.AccessToken);
        await RefreshAsync(winner.RefreshToken);
    }

    [PostgresFact]
    public async Task ConcurrentRotationAndLogoutNeverResurrectsSession()
    {
        var first = await LoginAsync("first");
        await using var rotationDb = new AppDbContext(Options);
        await using var revokeDb = new AppDbContext(Options);
        var list = await QueryAsync("{ mySessions { id } }", first.AccessToken);
        var id = Guid.Parse(list.GetProperty("mySessions")[0].GetProperty("id").GetString()!);
        var rotation = TryRotateAsync(rotationDb, first.RefreshToken);
        var revoke = Service(revokeDb).RevokeAsync(1, id);
        await Task.WhenAll(rotation, revoke);
        Assert.True(await revoke);
        var rotated = await rotation;
        if (rotated is not null) await AssertRefreshRejectedAsync(rotated.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("{ me { id } }", first.AccessToken)).StatusCode);
    }

    [PostgresFact]
    public async Task BanAndUnbanDoesNotRestoreAccessOrRefresh()
    {
        var first = await LoginAsync("first");
        await using var db = new AppDbContext(Options);
        var users = new UserService(db);
        await users.SetBanned(1, true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("{ me { id } }", first.AccessToken)).StatusCode);
        await AssertRefreshRejectedAsync(first.RefreshToken);
        await users.SetBanned(1, false);
        await AssertRefreshRejectedAsync(first.RefreshToken);
        await LoginAsync("first");
    }

    [PostgresFact]
    public async Task MigrationRollbackAndReapplyPreserveAccountsAndInvalidateSessions()
    {
        var first = await LoginAsync("first");
        await using var db = new AppDbContext(Options);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        await db.Database.MigrateAsync();
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.Empty(await db.UserSessions.ToListAsync());
        await AssertRefreshRejectedAsync(first.RefreshToken);
        await LoginAsync("first");
    }

    [PostgresFact]
    public async Task RegistrationCreatesUsableSessionAndWrongPasswordCreatesNone()
    {
        var registration = await QueryAsync("mutation($input: RegisterInput!) { register(input: $input) { accessToken refreshToken user { login } } }",
            variables: new { input = new { login = "registered", password = Password, name = "Registered" } });
        var tokens = ReadTokens(registration.GetProperty("register"));
        Assert.Equal("registered", registration.GetProperty("register").GetProperty("user").GetProperty("login").GetString());
        await QueryAsync("{ me { login } }", tokens.AccessToken);
        await RefreshAsync(tokens.RefreshToken);
        var badLogin = await SendAsync("mutation($input: LoginInput!) { login(input: $input) { accessToken } }",
            variables: new { input = new { login = "registered", password = "wrong-password" } });
        var payload = await badLogin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.TryGetProperty("errors", out _));
        await using var db = new AppDbContext(Options);
        Assert.Equal(1, await db.UserSessions.CountAsync());
    }

    [PostgresFact]
    public async Task ConcurrentRotationAndLogoutAllNeverResurrectsAnySession()
    {
        var first = await LoginAsync("first");
        var second = await LoginAsync("first");
        await using var db1 = new AppDbContext(Options);
        await using var db2 = new AppDbContext(Options);
        var rotation = TryRotateAsync(db1, first.RefreshToken);
        var logoutAll = Service(db2).RevokeAllAsync(1);
        await Task.WhenAll(rotation, logoutAll);
        var rotated = await rotation;
        if (rotated is not null) await AssertRefreshRejectedAsync(rotated.RefreshToken);
        await AssertRefreshRejectedAsync(second.RefreshToken);
        Assert.Empty(await db2.UserSessions.Where(s => s.RevokedAt == null).ToListAsync());
    }

    private static async Task<RefreshedSession?> TryRotateAsync(AppDbContext db, string token)
    {
        try { return await Service(db).RotateAsync(token); }
        catch (GraphQLException) { return null; }
    }
    private static UserSessionService Service(AppDbContext db) => new(db, new UserService(db), new AuthService(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Jwt:Key"] = Key, ["Jwt:Issuer"] = "session-tests", ["Jwt:Audience"] = "session-tests" }).Build()));

    private async Task<Tokens> LoginAsync(string login)
    {
        var data = await QueryAsync("mutation($input: LoginInput!) { login(input: $input) { accessToken refreshToken } }",
            variables: new { input = new { login, password = Password } });
        return ReadTokens(data.GetProperty("login"));
    }
    private async Task<Tokens> RefreshAsync(string token)
    {
        var data = await QueryAsync("mutation($token: String!) { refreshToken(refreshToken: $token) { accessToken refreshToken } }",
            variables: new { token });
        return ReadTokens(data.GetProperty("refreshToken"));
    }
    private async Task AssertRefreshRejectedAsync(string token)
    {
        var response = await SendAsync("mutation($token: String!) { refreshToken(refreshToken: $token) { accessToken } }", variables: new { token });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0);
    }
    private static Tokens ReadTokens(JsonElement data) => new(data.GetProperty("accessToken").GetString()!, data.GetProperty("refreshToken").GetString()!);
    private async Task<JsonElement> QueryAsync(string query, string? token = null, object? variables = null)
    {
        using var response = await SendAsync(query, token, variables);
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.TryGetProperty("errors", out var errors), errors.ToString());
        return payload.GetProperty("data").Clone();
    }
    private Task<HttpResponseMessage> SendAsync(string query, string? token = null, object? variables = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/graphql") { Content = JsonContent.Create(new { query, variables }) };
        request.Headers.UserAgent.ParseAdd("SessionTests/1.0");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        if (Directory.Exists(avatarDirectory)) Directory.Delete(avatarDirectory, recursive: true);
        if (databaseName is null) return;
        NpgsqlConnection.ClearAllPools();
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
        await command.ExecuteNonQueryAsync();
    }
    private record Tokens(string AccessToken, string RefreshToken);
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GUTV_SESSION_TEST_POSTGRES")))
            Skip = "Set GUTV_SESSION_TEST_POSTGRES to a local PostgreSQL server with CREATE DATABASE permission.";
    }
}
