using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using GUtv_backend_dotnet.Services.Telegram;
using HotChocolate;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;

namespace GUtv_backend_dotnet.Tests;

public class TelegramNotificationTests
{
    private const long OwnerChat = 101;
    private const long AdminChat = 201;
    private const long SecondAdminChat = 301;

    [Theory]
    [InlineData(UserRole.User)]
    [InlineData(UserRole.Admin)]
    public async Task CreatedConfirmationReachesLinkedOwnerRegardlessOfRole(UserRole ownerRole)
    {
        await using var fixture = await Fixture.CreateAsync(ownerRole: ownerRole);

        await fixture.Notifications.NotifyUserBookingCreated(await fixture.Db.Bookings.SingleAsync());

        var sent = Assert.Single(fixture.Handler.Requests);
        Assert.Equal(OwnerChat, sent.ChatId);
        Assert.Contains("Ваша заявка создана.", sent.Text);
        Assert.Contains("Бронирование #1", sent.Text);
        Assert.Contains("Camera", sent.Text);
        Assert.Contains("CAM-1", sent.Text);
        Assert.Contains("МСК", sent.Text);
        Assert.False(sent.HasParseMode);
        Assert.False(sent.Payload.TryGetProperty("reply_markup", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-101L)]
    public async Task OwnerWithoutPrivateChatReceivesNoCreatedOrUpdatedConfirmation(long? chatId)
    {
        await using var fixture = await Fixture.CreateAsync(ownerChat: chatId);
        var booking = await fixture.Db.Bookings.SingleAsync();

        await fixture.Notifications.NotifyUserBookingCreated(booking);
        await fixture.Notifications.NotifyUserBookingUpdated(booking, 1);

        Assert.Empty(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task UpdatedConfirmationGoesToOwnerAndIdentifiesAdministratorChanges(int actorId, bool byAdmin)
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Notifications.NotifyUserBookingUpdated(await fixture.Db.Bookings.SingleAsync(), actorId);

        var sent = Assert.Single(fixture.Handler.Requests);
        Assert.Equal(OwnerChat, sent.ChatId);
        Assert.Contains("Ваша заявка изменена.", sent.Text);
        Assert.Equal(byAdmin, sent.Text.Contains("Изменения внесены администратором.", StringComparison.Ordinal));
        Assert.False(sent.HasParseMode);
    }

    [Fact]
    public async Task OwnerConfirmationKeepsMarkupLiteralAndBoundsLongUnicodeFields()
    {
        await using var fixture = await Fixture.CreateAsync();
        var booking = await fixture.Db.Bookings.SingleAsync();
        booking.Reason = "<b>literal & reason</b> " + string.Concat(Enumerable.Repeat("😀", 3000));
        booking.Comment = "<script>literal & comment</script> " + string.Concat(Enumerable.Repeat("🚀", 1000));
        var model = await fixture.Db.EqModels.SingleAsync();
        model.Name = "Camera <&> " + string.Concat(Enumerable.Repeat("📷", 100));
        var item = await fixture.Db.EqItems.SingleAsync();
        item.InventoryNumber = "CAM<&>" + string.Concat(Enumerable.Repeat("🧰", 50));
        for (var id = 2; id <= 15; id++)
        {
            var extraItem = new EqItem
            {
                Id = id, EqModelId = model.Id, InventoryNumber = "Extra" + id, Operable = true
            };
            fixture.Db.BookingItems.Add(new BookingItem
            {
                BookingId = booking.Id, EqItem = extraItem,
                StartDate = booking.StartTime, EndDate = booking.EndTime
            });
        }
        await fixture.Db.SaveChangesAsync();

        await fixture.Notifications.NotifyUserBookingCreated(booking);

        var sent = Assert.Single(fixture.Handler.Requests);
        Assert.False(sent.HasParseMode);
        Assert.Contains("<b>literal & reason</b>", sent.Text);
        Assert.Contains("<script>literal & comment</script>", sent.Text);
        Assert.Contains("Camera <&>", sent.Text);
        Assert.DoesNotContain("&lt;", sent.Text);
        Assert.InRange(sent.Text.Length, 1, 4000);
        Assert.DoesNotContain("Extra15", sent.Text);
        AssertValidUtf16(sent.Text);
    }

    [Fact]
    public async Task BlockedOwnerDoesNotMakeNotificationThrow()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.BlockedChats.Add(OwnerChat);

        await fixture.Notifications.NotifyUserBookingCreated(await fixture.Db.Bookings.SingleAsync());

        var attempted = Assert.Single(fixture.Handler.Requests);
        Assert.Equal(OwnerChat, attempted.ChatId);
        Assert.False(attempted.Succeeded);
        Assert.Equal(BookingStatus.Pending, (await fixture.Db.Bookings.SingleAsync()).Status);
    }

    [Fact]
    public async Task StatusNotificationPreservesHtmlStatusTransitionAndAdminComment()
    {
        await using var fixture = await Fixture.CreateAsync();
        var booking = await fixture.Db.Bookings.SingleAsync();
        booking.Status = BookingStatus.Approved;
        booking.AdminComment = "Approved <carefully> & checked";
        await fixture.Db.SaveChangesAsync();

        await fixture.Notifications.NotifyUserBookingStatusChanged(booking, BookingStatus.Pending);

        var sent = Assert.Single(fixture.Handler.Requests);
        Assert.Equal(OwnerChat, sent.ChatId);
        Assert.Equal("Html", sent.Payload.GetProperty("parse_mode").GetString());
        Assert.Contains("<s>Ожидает</s> → <b>Одобрено</b>", sent.Text);
        Assert.Contains("Approved &lt;carefully&gt; &amp; checked", sent.Text);
        Assert.DoesNotContain("Ваша заявка создана", sent.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectAndCartCreateNotifyOwnerAndAdminsOnlyAfterCommit(bool fromCart)
    {
        await using var fixture = await Fixture.CreateAsync(seedBooking: false);

        var booking = await CreateBookingAsync(fixture, fromCart);

        Assert.Equal(1, await fixture.Db.Bookings.CountAsync());
        Assert.Equal(booking.Id, (await fixture.Db.Bookings.AsNoTracking().SingleAsync()).Id);
        AssertOwnerAndAdminNotifications(fixture, "Ваша заявка создана.");
        Assert.All(fixture.Handler.Requests, request => Assert.False(request.HadActiveTransaction));
        if (fromCart)
        {
            Assert.Empty(await fixture.Db.CartItems.ToListAsync());
            Assert.Equal("", (await fixture.Db.Carts.SingleAsync()).Reason);
        }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task DirectAndCartUpdateNotifyOriginalOwnerAfterCommit(bool fromCart, int actorId)
    {
        await using var fixture = await Fixture.CreateAsync();
        Booking booking;
        if (fromCart)
        {
            await fixture.Carts.PrepareBookingEditAsync(actorId, 1, actorId == 2);
            await fixture.Carts.SetCartDetailsAsync(actorId, new UpdateCartDetailsInput("Updated reason", null, null, null));
            booking = await fixture.Carts.UpdateBookingFromCartAsync(actorId, 1, actorId == 2);
        }
        else
        {
            booking = await fixture.Bookings.UpdateBookingAsync(1, CreateInput("Updated reason"), actorId, actorId == 2);
        }

        Assert.Equal(1, booking.UserId);
        Assert.Equal("Updated reason", (await fixture.Db.Bookings.AsNoTracking().SingleAsync()).Reason);
        AssertOwnerAndAdminNotifications(fixture, "Ваша заявка изменена.");
        var owner = Assert.Single(fixture.Handler.Requests, request => request.ChatId == OwnerChat);
        Assert.Equal(actorId == 2, owner.Text.Contains("Изменения внесены администратором.", StringComparison.Ordinal));
        Assert.All(fixture.Handler.Requests, request => Assert.False(request.HadActiveTransaction));
        if (fromCart) Assert.Empty(await fixture.Db.CartItems.ToListAsync());
    }

    [Fact]
    public async Task FailureForOneAdminDoesNotPreventOtherAdminsOrOwnerConfirmation()
    {
        await using var fixture = await Fixture.CreateAsync(seedBooking: false);
        fixture.Handler.BlockedChats.Add(AdminChat);

        var booking = await fixture.Bookings.CreateBookingAsync(CreateInput(), 1);

        Assert.Equal(BookingStatus.Pending, booking.Status);
        Assert.Equal(3, fixture.Handler.Requests.Count);
        Assert.False(Assert.Single(fixture.Handler.Requests, request => request.ChatId == AdminChat).Succeeded);
        Assert.True(Assert.Single(fixture.Handler.Requests, request => request.ChatId == SecondAdminChat).Succeeded);
        Assert.True(Assert.Single(fixture.Handler.Requests, request => request.ChatId == OwnerChat).Succeeded);
        Assert.Equal(1, await fixture.Db.Bookings.CountAsync());
    }

    [Fact]
    public async Task OwnerDeliveryFailureDoesNotUndoBookingOrAdminNotifications()
    {
        await using var fixture = await Fixture.CreateAsync(seedBooking: false);
        fixture.Handler.BlockedChats.Add(OwnerChat);

        var booking = await fixture.Bookings.CreateBookingAsync(CreateInput(), 1);

        Assert.Equal(BookingStatus.Pending, booking.Status);
        Assert.Equal(1, await fixture.Db.Bookings.CountAsync());
        Assert.Equal(2, fixture.Handler.Requests.Count(request => request.Succeeded));
        Assert.False(Assert.Single(fixture.Handler.Requests, request => request.ChatId == OwnerChat).Succeeded);
    }

    [Fact]
    public async Task AdministratorOwnerGetsBothPersonalConfirmationAndActionableAdminNotification()
    {
        await using var fixture = await Fixture.CreateAsync(seedBooking: false, ownerRole: UserRole.Admin);

        await fixture.Bookings.CreateBookingAsync(CreateInput(), 1);

        Assert.Equal(4, fixture.Handler.Requests.Count);
        var personal = Assert.Single(fixture.Handler.Requests,
            request => request.ChatId == OwnerChat && !request.HasParseMode);
        Assert.Contains("Ваша заявка создана.", personal.Text);
        var admin = Assert.Single(fixture.Handler.Requests,
            request => request.ChatId == OwnerChat && request.HasParseMode);
        Assert.True(admin.Payload.TryGetProperty("reply_markup", out _));
    }

    [Fact]
    public async Task CreatedConfirmationReportsPersistedStatusIfDecisionAlreadyChangedIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var booking = await fixture.Db.Bookings.SingleAsync();
        booking.Status = BookingStatus.Approved;
        await fixture.Db.SaveChangesAsync();

        await fixture.Notifications.NotifyUserBookingCreated(booking);

        var sent = Assert.Single(fixture.Handler.Requests);
        Assert.Contains("Ваша заявка создана.", sent.Text);
        Assert.Contains("Статус: Одобрено", sent.Text);
        Assert.DoesNotContain("ожидает решения", sent.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitFailureRollsBackSavedCheckoutAndSendsNothing(bool fromCart)
    {
        var failure = new CommitFailureInterceptor();
        await using var fixture = await Fixture.CreateAsync(seedBooking: false, transactionInterceptor: failure);
        if (fromCart) await PrepareCartAsync(fixture);
        failure.Enabled = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fromCart
            ? fixture.Carts.CreateBookingFromCartAsync(1)
            : fixture.Bookings.CreateBookingAsync(CreateInput(), 1));

        Assert.True(failure.Intercepted);
        Assert.Equal(0, await fixture.Db.Bookings.CountAsync());
        Assert.Equal(0, await fixture.Db.BookingItems.CountAsync());
        Assert.Empty(fixture.Handler.Requests);
        Assert.Null(fixture.Db.Database.CurrentTransaction);
        if (fromCart)
        {
            Assert.Single(await fixture.Db.CartItems.AsNoTracking().ToListAsync());
            Assert.Equal("Created reason", (await fixture.Db.Carts.AsNoTracking().SingleAsync()).Reason);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDirectAndCartCheckoutDoNotSendNotificationsOrClearCart(bool fromCart)
    {
        await using var fixture = await Fixture.CreateAsync(seedBooking: false);
        if (fromCart)
        {
            await PrepareCartAsync(fixture, quantity: 2);
            await Assert.ThrowsAsync<GraphQLException>(() => fixture.Carts.CreateBookingFromCartAsync(1));
            Assert.Equal(2, (await fixture.Db.CartItems.AsNoTracking().SingleAsync()).Quantity);
            Assert.Equal("Created reason", (await fixture.Db.Carts.AsNoTracking().SingleAsync()).Reason);
        }
        else
        {
            var input = CreateInput() with { Equipment = [new CreateBookingEquipmentInput("Camera", 2)] };
            await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.CreateBookingAsync(input, 1));
        }

        Assert.Equal(0, await fixture.Db.Bookings.CountAsync());
        Assert.Empty(fixture.Handler.Requests);
        Assert.Null(fixture.Db.Database.CurrentTransaction);
    }

    private static void AssertOwnerAndAdminNotifications(Fixture fixture, string ownerHeading)
    {
        Assert.Equal(3, fixture.Handler.Requests.Count);
        var owner = Assert.Single(fixture.Handler.Requests, request => request.ChatId == OwnerChat);
        Assert.Contains(ownerHeading, owner.Text);
        Assert.False(owner.HasParseMode);
        foreach (var adminChat in new[] { AdminChat, SecondAdminChat })
        {
            var admin = Assert.Single(fixture.Handler.Requests, request => request.ChatId == adminChat);
            Assert.Equal("Html", admin.Payload.GetProperty("parse_mode").GetString());
            var callbacks = admin.Payload.GetProperty("reply_markup").GetProperty("inline_keyboard")[0];
            Assert.StartsWith("booking:approve:", callbacks[0].GetProperty("callback_data").GetString());
            Assert.StartsWith("booking:reject:", callbacks[1].GetProperty("callback_data").GetString());
        }
    }

    private static async Task<Booking> CreateBookingAsync(Fixture fixture, bool fromCart)
    {
        if (!fromCart) return await fixture.Bookings.CreateBookingAsync(CreateInput(), 1);
        await PrepareCartAsync(fixture);
        return await fixture.Carts.CreateBookingFromCartAsync(1);
    }

    private static async Task PrepareCartAsync(Fixture fixture, int quantity = 1)
    {
        var input = CreateInput();
        await fixture.Carts.SetCartDetailsAsync(1, new UpdateCartDetailsInput(input.Reason, input.StartTime, input.EndTime, input.Comment));
        await fixture.Carts.AddCartItemAsync(1, 1, quantity);
    }

    private static CreateBookingInput CreateInput(string reason = "Created reason") => new(
        reason, DateTime.UtcNow.AddDays(7), DateTime.UtcNow.AddDays(8), "Owner comment",
        [new CreateBookingEquipmentInput("Camera", 1)]);

    private static void AssertValidUtf16(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                Assert.True(index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]));
                index++;
            }
            else Assert.False(char.IsLowSurrogate(text[index]));
        }
        Assert.DoesNotContain('\uFFFD', text);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly HttpClient _httpClient;
        public AppDbContext Db { get; }
        public RecordingHandler Handler { get; }
        public TelegramNotificationService Notifications { get; }
        public BookingService Bookings { get; }
        public CartService Carts { get; }

        private Fixture(SqliteConnection connection, AppDbContext db)
        {
            _connection = connection;
            Db = db;
            Handler = new RecordingHandler(db);
            _httpClient = new HttpClient(Handler);
            var bot = new TelegramBotClient("123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi", _httpClient);
            Notifications = new TelegramNotificationService(bot, db, NullLogger<TelegramNotificationService>.Instance);
            Bookings = new BookingService(db, Notifications);
            Carts = new CartService(db, Bookings);
        }

        public static async Task<Fixture> CreateAsync(bool seedBooking = true,
            UserRole ownerRole = UserRole.User, long? ownerChat = OwnerChat,
            DbTransactionInterceptor? transactionInterceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection)
                .AddInterceptors(new AdvisoryLockInterceptor());
            if (transactionInterceptor is not null) options.AddInterceptors(transactionInterceptor);
            var db = new NotificationTestDbContext(options.Options);
            await db.Database.EnsureCreatedAsync();
            db.Users.AddRange(
                new User { Id = 1, Login = "owner", Name = "Owner", Role = ownerRole, TelegramChatId = ownerChat },
                new User { Id = 2, Login = "admin", Name = "Admin", Role = UserRole.Admin, TelegramChatId = AdminChat },
                new User { Id = 3, Login = "second-admin", Name = "Second admin", Role = UserRole.Admin, TelegramChatId = SecondAdminChat });
            db.EqModels.Add(new EqModel { Id = 1, Name = "Camera", Access = EqAccess.User });
            db.EqItems.Add(new EqItem { Id = 1, EqModelId = 1, InventoryNumber = "CAM-1", Operable = true });
            if (seedBooking)
            {
                var input = CreateInput();
                db.Bookings.Add(new Booking
                {
                    Id = 1, UserId = 1, Reason = input.Reason, StartTime = input.StartTime,
                    EndTime = input.EndTime, Comment = input.Comment, Status = BookingStatus.Pending,
                    BookingItems = [new BookingItem { EqItemId = 1, StartDate = input.StartTime, EndDate = input.EndTime }]
                });
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            _httpClient.Dispose();
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class NotificationTestDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // Preserve login uniqueness while substituting SQLite's equivalent normalization.
            modelBuilder.Entity<User>().Property(user => user.NormalizedLogin)
                .HasComputedColumnSql("lower(trim(\"Login\"))", stored: true);
        }
    }

    private sealed class AdvisoryLockInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            // PostgreSQL advisory locks are orthogonal to notification timing; keep real SQLite transactions.
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
                return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CommitFailureInterceptor : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public bool Intercepted { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                Intercepted = true;
                throw new InvalidOperationException("Simulated failure after saving changes, before commit");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed record SentRequest(long ChatId, string Text, JsonElement Payload,
        bool Succeeded, bool HadActiveTransaction)
    {
        public bool HasParseMode => Payload.TryGetProperty("parse_mode", out var mode)
            && mode.ValueKind != JsonValueKind.Null && !string.IsNullOrEmpty(mode.GetString());
    }

    private sealed class RecordingHandler(AppDbContext db) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];
        public HashSet<long> BlockedChats { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.EndsWith("/sendMessage", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var payload = body.RootElement.Clone();
            var chatIdValue = payload.GetProperty("chat_id");
            var chatId = chatIdValue.ValueKind == JsonValueKind.Number
                ? chatIdValue.GetInt64() : long.Parse(chatIdValue.GetString()!);
            var succeeded = !BlockedChats.Contains(chatId);
            Requests.Add(new SentRequest(chatId, payload.GetProperty("text").GetString()!, payload,
                succeeded, db.Database.CurrentTransaction is not null));
            var json = succeeded
                ? JsonSerializer.Serialize(new { ok = true, result = new { message_id = Requests.Count, date = 0,
                    chat = new { id = chatId, type = "private" }, text = "sent" } })
                : "{\"ok\":false,\"error_code\":403,\"description\":\"Forbidden: bot was blocked by the user\"}";
            return new HttpResponseMessage(succeeded ? HttpStatusCode.OK : HttpStatusCode.Forbidden)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
