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
using GUtv_backend_dotnet.Services.Telegram;
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
using Telegram.Bot;

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
        host.Services.AddSingleton<RequestRateLimiter>();
        host.Services.AddSingleton<AvatarImageStore>();
        host.Services.AddScoped<UserAvatarService>();
        host.Services.AddScoped<UserSessionService>();
        host.Services.AddScoped<AuthService>();
        host.Services.AddScoped<EquipmentService>();
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
        // Cookies are passed by hand, so every request shows exactly which token it carries.
        client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(address) };
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
    public async Task BanPreservesLoginAccessAndRefresh()
    {
        var first = await LoginAsync("first");
        await using var db = new AppDbContext(Options);
        await new UserService(db).SetRole(2, UserRole.Admin);
        var admin = await LoginAsync("second");
        var banned = await QueryAsync("""
            mutation { setUserBanned(userId: 1, banned: true, reason: "  Нарушение правил  ") { banned banReason } }
            """, admin.AccessToken);
        Assert.True(banned.GetProperty("setUserBanned").GetProperty("banned").GetBoolean());
        Assert.Equal("Нарушение правил", banned.GetProperty("setUserBanned").GetProperty("banReason").GetString());
        var me = await QueryAsync("{ me { id banned banReason } }", first.AccessToken);
        Assert.True(me.GetProperty("me").GetProperty("banned").GetBoolean());
        var rotated = await RefreshAsync(first.RefreshToken);
        await LoginAsync("first");
        var unbanned = await QueryAsync("""
            mutation { setUserBanned(userId: 1, banned: false) { banned banReason } }
            """, admin.AccessToken);
        Assert.False(unbanned.GetProperty("setUserBanned").GetProperty("banned").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unbanned.GetProperty("setUserBanned").GetProperty("banReason").ValueKind);
        await RefreshAsync(rotated.RefreshToken);
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
        var (tokens, registration) = await SignInAsync(
            "mutation($input: RegisterInput!) { register(input: $input) { accessToken refreshToken user { login } } }",
            new { input = new { login = "registered", password = Password, name = "Registered" } }, "register");
        Assert.Equal("registered", registration.GetProperty("user").GetProperty("login").GetString());
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

    [PostgresFact]
    public async Task ConcurrentExplicitInventoryRequestsCannotDoubleBookAndMissingChoiceIsRejected()
    {
        int modelId, selectedId, automaticId;
        await using (var db = new AppDbContext(Options))
        {
            var model = new EqModel { Name = "Headphones", EqItems = [
                new EqItem { InventoryNumber = "HEAD-001" },
                new EqItem { InventoryNumber = "HEAD-002" }
            ] };
            db.EqModels.Add(model);
            await db.SaveChangesAsync();
            modelId = model.Id;
            automaticId = model.EqItems[0].Id;
            selectedId = model.EqItems[1].Id;
        }
        var start = DateTime.UtcNow.AddDays(10);
        var input = new CreateBookingInput("Inventory concurrency", start, start.AddHours(2), null,
            [new CreateBookingEquipmentInput(modelId, 1, [selectedId])]);

        async Task<bool> BookAsync()
        {
            await using var db = new AppDbContext(Options);
            var service = BookingServiceFor(db);
            try { await service.CreateBookingAsync(input, 1); return true; }
            catch (GraphQLException error)
            {
                Assert.Contains(error.Errors, item => item.Message.Contains("HEAD-002") && item.Message.Contains("занят"));
                return false;
            }
        }

        var outcomes = await Task.WhenAll(BookAsync(), BookAsync());
        Assert.Single(outcomes, success => success);
        Assert.Single(outcomes, success => !success);
        await using var verify = new AppDbContext(Options);
        var assigned = await verify.BookingItems.AsNoTracking().SingleAsync();
        Assert.Equal(selectedId, assigned.EqItemId);
        Assert.True(assigned.IsExplicitlySelected);
        await Assert.ThrowsAsync<GraphQLException>(() => BookingServiceFor(verify).CreateBookingAsync(input with
        {
            Equipment = [new CreateBookingEquipmentInput(modelId, 1)]
        }, 2));
        var other = await BookingServiceFor(verify).CreateBookingAsync(input with
        {
            Equipment = [new CreateBookingEquipmentInput(modelId, 1, [automaticId])]
        }, 2);
        Assert.Equal(automaticId, Assert.Single(other.BookingItems).EqItemId);

        static BookingService BookingServiceFor(AppDbContext db) => new(db, new TelegramNotificationService(
            new TelegramBotClient("123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi"), db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TelegramNotificationService>.Instance));
    }

    [PostgresFact]
    public async Task RefreshTokenTravelsOnlyInTheHttpOnlyCookie()
    {
        var first = await LoginAsync("first");

        // The cookie alone rotates the session; the response body carries no refresh token.
        using var refreshed = await SendAsync("mutation { refreshToken { accessToken refreshToken } }", cookie: first.RefreshToken);
        var body = (await refreshed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("refreshToken");
        Assert.Equal(JsonValueKind.Null, body.GetProperty("refreshToken").ValueKind);
        var accessToken = body.GetProperty("accessToken").GetString()!;
        var rotated = ReadRefreshCookie(refreshed);
        Assert.NotEqual(first.RefreshToken, rotated);
        await QueryAsync("{ me { login } }", accessToken);

        // The replaced cookie is refused, and that refusal does not touch the browser's cookie.
        using var stale = await SendAsync("mutation { refreshToken { accessToken } }", cookie: first.RefreshToken);
        Assert.True((await stale.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("errors", out _));
        Assert.False(stale.Headers.Contains("Set-Cookie"));

        // No cookie and no argument: nothing to refresh.
        using var missing = await SendAsync("mutation { refreshToken { accessToken } }");
        Assert.True((await missing.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("errors", out _));

        // Signing out revokes the session and removes the cookie.
        using var logout = await SendAsync("mutation { logout }", accessToken);
        Assert.True((await logout.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("logout").GetBoolean());
        var removal = Assert.Single(logout.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(RefreshTokenCookie.Name + "=;", removal);
        using var afterLogout = await SendAsync("mutation { refreshToken { accessToken } }", cookie: rotated);
        Assert.True((await afterLogout.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("errors", out _));
    }

    [PostgresFact]
    public async Task UnitsAreDeletedOrArchivedUnderTheBookingLock()
    {
        await using var db = new AppDbContext(Options);
        var model = new EqModel { Name = "Camera" };
        db.EqItems.AddRange(
            new EqItem { EqModel = model, InventoryNumber = "0-001-01" },
            new EqItem { EqModel = model, InventoryNumber = "0-001-02" },
            new EqItem { EqModel = model, InventoryNumber = "0-001-03" });
        await db.SaveChangesAsync();
        var ids = await db.EqItems.OrderBy(item => item.Id).Select(item => item.Id).ToListAsync();
        var owner = await db.Users.OrderBy(user => user.Id).Select(user => user.Id).FirstAsync();
        var start = DateTime.UtcNow.AddDays(10);
        db.Carts.Add(new Cart
        {
            UserId = owner,
            Items = [new CartItem { EqModelId = model.Id, Quantity = 2, SelectedItemIds = [ids[0], ids[1]] }]
        });
        db.Bookings.AddRange(
            new Booking
            {
                UserId = owner, Reason = "Finished", Status = BookingStatus.Completed, StartTime = start, EndTime = start.AddHours(1),
                BookingItems = [new BookingItem { EqItemId = ids[1], StartDate = start, EndDate = start.AddHours(1) }]
            },
            new Booking
            {
                UserId = owner, Reason = "Active", Status = BookingStatus.Approved, StartTime = start, EndTime = start.AddHours(1),
                BookingItems = [new BookingItem { EqItemId = ids[2], StartDate = start, EndDate = start.AddHours(1) }]
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var equipment = new EquipmentService(db);

        // Never booked, and not the last unit of its model: deleted outright.
        Assert.True(await equipment.DeleteItemAsync(ids[0]));
        // Booked in the past: archived, so the finished booking keeps its unit.
        Assert.True(await equipment.DeleteItemAsync(ids[1]));
        // Held by an active booking: refused.
        await Assert.ThrowsAsync<GraphQLException>(() => equipment.DeleteItemAsync(ids[2]));

        db.ChangeTracker.Clear();
        Assert.Equal(new[] { ids[1], ids[2] }, await db.EqItems.OrderBy(item => item.Id).Select(item => item.Id).ToListAsync());
        Assert.NotNull((await db.EqItems.SingleAsync(item => item.Id == ids[1])).ArchivedAt);
        Assert.Equal(new[] { ids[2] }, (await equipment.GetItemsByModelAsync(model.Id)).Select(item => item.Id));
        var cartItem = await db.CartItems.AsNoTracking().SingleAsync();
        Assert.Empty(cartItem.SelectedItemIds);
        Assert.Equal(0, cartItem.Quantity);
        // A new unit takes the next number: the archived one keeps its own for the history.
        Assert.Equal("0-" + model.Id.ToString("D3") + "-04", (await equipment.CreateItemAsync(model.Id)).InventoryNumber);
    }

    private async Task<Tokens> LoginAsync(string login)
    {
        var (tokens, _) = await SignInAsync(
            "mutation($input: LoginInput!) { login(input: $input) { accessToken refreshToken } }",
            new { input = new { login, password = Password } }, "login");
        return tokens;
    }
    // Signing in answers with the access token only; the refresh token arrives as a cookie.
    private async Task<(Tokens Tokens, JsonElement Payload)> SignInAsync(string query, object variables, string field)
    {
        using var response = await SendAsync(query, variables: variables);
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.TryGetProperty("errors", out var errors), errors.ToString());
        var data = payload.GetProperty("data").GetProperty(field).Clone();
        Assert.Equal(JsonValueKind.Null, data.GetProperty("refreshToken").ValueKind);
        return (new Tokens(data.GetProperty("accessToken").GetString()!, ReadRefreshCookie(response)), data);
    }
    private static string ReadRefreshCookie(HttpResponseMessage response)
    {
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        var pair = cookie.Split(';')[0];
        Assert.StartsWith(RefreshTokenCookie.Name + "=", pair);
        return Uri.UnescapeDataString(pair[(RefreshTokenCookie.Name.Length + 1)..]);
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
    private Task<HttpResponseMessage> SendAsync(string query, string? token = null, object? variables = null, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/graphql") { Content = JsonContent.Create(new { query, variables }) };
        request.Headers.UserAgent.ParseAdd("SessionTests/1.0");
        if (cookie is not null)
            request.Headers.Add("Cookie", $"{RefreshTokenCookie.Name}={Uri.EscapeDataString(cookie)}");
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
