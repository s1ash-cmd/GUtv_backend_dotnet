using System.Data.Common;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.GraphQL.Mutations;
using GUtv_backend_dotnet.Migrations;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using GUtv_backend_dotnet.Services.Telegram;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace GUtv_backend_dotnet.Tests;

public class BookingAdminCommentTests
{
    private const string AdminName = "Test administrator";
    private const long AdminChat = 201;
    private const string BotToken = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi";
    private const string CleanupMigration = "20261001000000_ClearSyntheticAdminComments";

    public static TheoryData<string?, string?> Comments => new()
    {
        { null, null },
        { "", null },
        { " \t\r\n ", null },
        { "  Checked carefully  ", AdminName + ": Checked carefully" }
    };

    [Theory]
    [MemberData(nameof(Comments))]
    public void SharedFormatterDoesNotInventAnEmptyComment(string? comment, string? expected)
    {
        Assert.Equal(expected, BookingAdminCommentFormatter.Format(new Models.User { Name = AdminName }, comment));
    }

    public static IEnumerable<object?[]> MutationCases()
    {
        foreach (var path in new[] { "web", "telegram-api" })
        foreach (var reject in new[] { false, true })
        foreach (var comments in Comments)
            yield return [path, reject, comments[0], comments[1]];
    }

    [Theory]
    [MemberData(nameof(MutationCases))]
    public async Task WebAndTelegramGraphqlDecisionsPersistOnlyRealComments(
        string path, bool reject, string? comment, string? expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userService = scope.ServiceProvider.GetRequiredService<UserService>();
        var bookings = scope.ServiceProvider.GetRequiredService<BookingService>();
        var mutations = new BookingMutations();
        if (path == "telegram-api")
        {
            var security = new BotSecurityService(fixture.Configuration);
            if (reject)
                await mutations.RejectBookingByTelegram(BotToken, AdminChat, 1, comment, security, userService, bookings);
            else
                await mutations.ApproveBookingByTelegram(BotToken, AdminChat, 1, comment, security, userService, bookings);
        }
        else
        {
            var accessor = new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "2"), new Claim(ClaimTypes.Role, "Admin")], "Bearer"))
                }
            };
            var equipment = scope.ServiceProvider.GetRequiredService<EquipmentService>();
            if (reject)
                await mutations.RejectBooking(1, comment, accessor, equipment, userService, bookings);
            else
                await mutations.ApproveBooking(1, comment, accessor, equipment, userService, bookings);
        }

        db.ChangeTracker.Clear();
        var persisted = await db.Bookings.SingleAsync();
        Assert.Equal(reject ? BookingStatus.Cancelled : BookingStatus.Approved, persisted.Status);
        Assert.Equal(expected, persisted.AdminComment);
    }

    [Theory]
    [InlineData(false, "-", null)]
    [InlineData(false, "", null)]
    [InlineData(false, " \t\r\n ", null)]
    [InlineData(false, "  Checked carefully  ", AdminName + ": Checked carefully")]
    [InlineData(true, "-", null)]
    [InlineData(true, "", null)]
    [InlineData(true, " \t\r\n ", null)]
    [InlineData(true, "  Checked carefully  ", AdminName + ": Checked carefully")]
    public async Task LiveTelegramCallbackAndCommentReplyPersistOnlyRealComments(
        bool reject, string comment, string? expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        var handler = new TelegramUpdateHandler(fixture.Provider,
            NullLogger<TelegramUpdateHandler>.Instance, fixture.Configuration);
        var chat = new Chat { Id = AdminChat, Type = ChatType.Private };
        var sender = new Telegram.Bot.Types.User { Id = AdminChat, Username = "admin" };

        await handler.HandleUpdateAsync(fixture.Bot, new Update
        {
            CallbackQuery = new CallbackQuery
            {
                Id = "decision", From = sender,
                Data = reject ? "booking:reject:1" : "booking:approve:1",
                Message = new Message { Id = 10, Chat = chat }
            }
        }, CancellationToken.None);
        var promptId = fixture.HttpHandler.LastMessageId;
        Assert.True(promptId > 0);
        await handler.HandleUpdateAsync(fixture.Bot, new Update
        {
            Message = new Message
            {
                Id = 20, Chat = chat, From = sender, Text = comment,
                ReplyToMessage = new Message { Id = promptId, Chat = chat }
            }
        }, CancellationToken.None);

        using var scope = fixture.Provider.CreateScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Bookings.SingleAsync();
        Assert.Equal(reject ? BookingStatus.Cancelled : BookingStatus.Approved, persisted.Status);
        Assert.Equal(expected, persisted.AdminComment);
    }

    [Fact]
    public async Task LegacyCleanupRemovesExactNameOnlyCommentsRegardlessOfCurrentRoleAndRetainsRealText()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.AddRange(
            new Models.User { Id = 3, Login = "former-admin", Name = "Former administrator", Role = UserRole.User },
            new Models.User { Id = 4, Login = "name-with-colon", Name = AdminName + ": Checked carefully", Role = UserRole.User });
        var comments = new string?[]
        {
            AdminName, "Former administrator", AdminName + ": Checked carefully",
            "Former administrator: Historic note", "Unknown old name", " " + AdminName,
            AdminName.ToUpperInvariant(), null, "", "Other comment"
        };
        var first = await db.Bookings.SingleAsync();
        first.AdminComment = comments[0];
        for (var index = 1; index < comments.Length; index++)
            db.Bookings.Add(new Booking { Id = index + 1, UserId = 1, Reason = "Legacy", AdminComment = comments[index] });
        await db.SaveChangesAsync();
        var migration = new ClearSyntheticAdminComments();

        foreach (var sql in migration.UpOperations.OfType<SqlOperation>())
            await db.Database.ExecuteSqlRawAsync(sql.Sql);

        db.ChangeTracker.Clear();
        var cleaned = await db.Bookings.OrderBy(b => b.Id).Select(b => b.AdminComment).ToListAsync();
        Assert.Null(cleaned[0]);
        Assert.Null(cleaned[1]);
        Assert.Equal(comments.Skip(2), cleaned.Skip(2));
        Assert.Empty(migration.DownOperations);
    }

    [Fact]
    public void CleanupMigrationIsDiscoverableAndGeneratesOnlyTheTargetedDataUpdate()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);

        Assert.Contains(CleanupMigration, db.Database.GetMigrations());
        var sql = db.GetService<IMigrator>().GenerateScript("20260924124716_AddAnnouncements", CleanupMigration);
        Assert.Contains("UPDATE \"Bookings\"", sql);
        Assert.Contains("\"Users\".\"Name\" = \"Bookings\".\"AdminComment\"", sql);
        Assert.Contains("\"AdminComment\" NOT LIKE '%:%'", sql);
        Assert.DoesNotContain("\"Role\"", sql);
        Assert.DoesNotContain("ALTER TABLE", sql);
        Assert.DoesNotContain("DROP", sql);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly HttpClient _httpClient;
        public ServiceProvider Provider { get; }
        public IConfiguration Configuration { get; }
        public ITelegramBotClient Bot { get; }
        public RecordingTelegramHandler HttpHandler { get; }

        private Fixture(SqliteConnection connection)
        {
            _connection = connection;
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["BotConfiguration:BotToken"] = BotToken }).Build();
            HttpHandler = new RecordingTelegramHandler();
            _httpClient = new HttpClient(HttpHandler);
            Bot = new TelegramBotClient(BotToken, _httpClient);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Configuration);
            services.AddSingleton(Bot);
            services.AddScoped<AppDbContext>(_ => new CommentTestDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection)
                    .AddInterceptors(new AdvisoryLockInterceptor()).Options));
            services.AddScoped<UserService>();
            services.AddScoped<EquipmentService>();
            services.AddScoped<BookingService>();
            services.AddScoped<TelegramNotificationService>();
            services.AddSingleton<AnnouncementBotHandler>();
            Provider = services.BuildServiceProvider();
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var fixture = new Fixture(connection);
            using var scope = fixture.Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Users.AddRange(
                new Models.User { Id = 1, Login = "owner", Name = "Owner", Role = UserRole.User },
                new Models.User { Id = 2, Login = "admin", Name = AdminName, Role = UserRole.Admin,
                    TelegramChatId = AdminChat, TelegramUsername = "admin" });
            db.Bookings.Add(new Booking
            {
                Id = 1, UserId = 1, Reason = "Test booking", Status = BookingStatus.Pending,
                StartTime = DateTime.UtcNow.AddDays(7), EndTime = DateTime.UtcNow.AddDays(8)
            });
            await db.SaveChangesAsync();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            _httpClient.Dispose();
            await _connection.DisposeAsync();
        }
    }

    private sealed class CommentTestDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Models.User>().Property(user => user.NormalizedLogin)
                .HasComputedColumnSql("lower(trim(\"Login\"))", stored: true);
        }
    }

    private sealed class AdvisoryLockInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal)
                ? InterceptionResult<int>.SuppressWithResult(1) : result);
    }

    private sealed class RecordingTelegramHandler : HttpMessageHandler
    {
        public int LastMessageId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            string json;
            if (request.RequestUri!.AbsolutePath.EndsWith("/answerCallbackQuery", StringComparison.Ordinal))
            {
                json = "{\"ok\":true,\"result\":true}";
            }
            else
            {
                Assert.EndsWith("/sendMessage", request.RequestUri.AbsolutePath);
                var chatId = body.RootElement.GetProperty("chat_id");
                var id = chatId.ValueKind == JsonValueKind.Number ? chatId.GetInt64() : long.Parse(chatId.GetString()!);
                json = JsonSerializer.Serialize(new
                {
                    ok = true,
                    result = new { message_id = ++LastMessageId, date = 0, chat = new { id, type = "private" }, text = "sent" }
                });
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
