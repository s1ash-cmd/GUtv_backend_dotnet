using System.Data.Common;
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

public class BookingAllocationTests
{
    private static readonly DateTime Start = new(2030, 1, 10, 10, 0, 0, DateTimeKind.Utc);
    private static CreateBookingInput Input(params CreateBookingEquipmentInput[] equipment) =>
        new("Съемка", Start, Start.AddHours(2), null, equipment);

    [Fact]
    public async Task ModelIdsSurviveRenamesAndDisambiguateIdenticalNames()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = await fixture.Db.EqModels.FindAsync(1);
        model!.Name = "Renamed camera";
        fixture.Db.EqModels.Add(new EqModel { Id = 2, Name = model.Name });
        fixture.Db.EqItems.Add(new EqItem { Id = 3, EqModelId = 2, InventoryNumber = "OTHER-1" });
        await fixture.Db.SaveChangesAsync();

        var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(2, 1)), 1);

        Assert.Equal(3, Assert.Single(booking.BookingItems).EqItemId);
        Assert.Equal("Renamed camera", booking.BookingItems.Single().EqItem.EqModel.Name);
    }

    [Fact]
    public async Task DuplicateIdsAreCombinedAndEachModelIsLockedOnceInIdOrder()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.EqModels.Add(new EqModel { Id = 2, Name = "Another camera" });
        fixture.Db.EqItems.Add(new EqItem { Id = 3, EqModelId = 2, InventoryNumber = "OTHER-1" });
        await fixture.Db.SaveChangesAsync();

        var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(2, 1), new CreateBookingEquipmentInput(1, 1), new CreateBookingEquipmentInput(1, 1)), 1);

        Assert.Equal(3, booking.BookingItems.Count);
        Assert.Equal(3, booking.BookingItems.Select(item => item.EqItemId).Distinct().Count());
        Assert.Equal(new[] { 1, 2 }, fixture.Locks.ModelIds);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(999, 1)]
    public async Task InvalidRequestsLeaveNoBooking(int modelId, int quantity)
    {
        await using var fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<GraphQLException>(() =>
            fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(modelId, quantity)), 1));

        Assert.Empty(await fixture.Db.Bookings.ToListAsync());
    }

    [Fact]
    public async Task CombinedQuantityOverflowIsReportedAsAValidationError()
    {
        await using var fixture = await Fixture.CreateAsync();
        var error = await Assert.ThrowsAsync<GraphQLException>(() =>
            fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, int.MaxValue), new CreateBookingEquipmentInput(1, 1)), 1));
        Assert.Contains(error.Errors, item => item.Message.Contains("Слишком большое количество"));
    }

    [Theory]
    [InlineData(BookingStatus.Pending, true, true)]
    [InlineData(BookingStatus.Approved, true, true)]
    [InlineData(BookingStatus.Cancelled, true, false)]
    [InlineData(BookingStatus.Completed, true, false)]
    [InlineData(BookingStatus.Pending, false, false)]
    public async Task AvailabilityAndAllocationAgreeOnStatusesAndAdjacentIntervals(
        BookingStatus status, bool overlaps, bool blocked)
    {
        await using var fixture = await Fixture.CreateAsync();
        var begin = overlaps ? Start : Start.AddHours(2);
        fixture.Db.Bookings.Add(new Booking
        {
            UserId = 1, Reason = "Existing", Status = status,
            StartTime = begin, EndTime = begin.AddHours(2),
            BookingItems = [new BookingItem { EqItemId = 1, StartDate = begin, EndDate = begin.AddHours(2) }]
        });
        (await fixture.Db.EqItems.FindAsync(2))!.Operable = false;
        await fixture.Db.SaveChangesAsync();

        var available = await new EquipmentService(fixture.Db).GetAvailableItemsByModelAsync(1, Start, Start.AddHours(2));
        Assert.Equal(blocked ? 0 : 1, available.Count);
        if (blocked)
            await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1)), 1));
        else
        {
            var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1)), 1);
            Assert.Equal(available.Single().Id, Assert.Single(booking.BookingItems).EqItemId);
        }
    }

    [Fact]
    public async Task EditReusesItsOwnEquipmentButStillExcludesOtherBookings()
    {
        await using var fixture = await Fixture.CreateAsync();
        var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1)), 1);
        await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1)), 2);

        var updated = await fixture.Bookings.UpdateBookingAsync(booking.Id, Input(new CreateBookingEquipmentInput(1, 1)), 1, false);
        Assert.Equal(booking.BookingItems.Single().EqItemId, Assert.Single(updated.BookingItems).EqItemId);
        Assert.Equal(booking.Revision + 1, updated.Revision);
        await Assert.ThrowsAsync<GraphQLException>(() =>
            fixture.Bookings.UpdateBookingAsync(booking.Id, Input(new CreateBookingEquipmentInput(1, 2)), 1, false));
        Assert.Single((await fixture.Bookings.GetBookingByIdAsync(booking.Id, 1, false)).BookingItems);
    }

    [Fact]
    public async Task CreationAndEditingKeepOsnovaWarningsAndRoninPermissions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var model = (await fixture.Db.EqModels.FindAsync(1))!;
        model.Access = EqAccess.Osnova;
        await fixture.Db.SaveChangesAsync();
        var booking = await fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1)), 1);
        var updated = await fixture.Bookings.UpdateBookingAsync(booking.Id, Input(new CreateBookingEquipmentInput(1, 1)), 1, false);
        Assert.Contains("missingOsnovaAccess_1", booking.WarningsJson);
        Assert.Contains("missingOsnovaAccess_1", updated.WarningsJson);

        model.Access = EqAccess.Ronin;
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.CreateBookingAsync(Input(new CreateBookingEquipmentInput(1, 1)), 1));
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Bookings.UpdateBookingAsync(booking.Id, Input(new CreateBookingEquipmentInput(1, 1)), 1, false));
        var granted = await fixture.Bookings.UpdateBookingAsync(booking.Id, Input(new CreateBookingEquipmentInput(1, 1)), 2, true);
        Assert.Single(granted.BookingItems);
        Assert.DoesNotContain("missingOsnovaAccess", granted.WarningsJson);
    }

    private sealed class Fixture(SqliteConnection connection, AppDbContext db, LockInterceptor locks) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public LockInterceptor Locks { get; } = locks;
        public BookingService Bookings { get; } = new(db, new TelegramNotificationService(
            new TelegramBotClient("123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi"), db,
            NullLogger<TelegramNotificationService>.Instance));

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var locks = new LockInterceptor();
            var db = new TestDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection).AddInterceptors(locks).Options);
            await db.Database.EnsureCreatedAsync();
            // No linked Telegram accounts, so these tests never send network requests.
            db.Users.AddRange(new User { Id = 1, Login = "owner", Name = "Owner" },
                new User { Id = 2, Login = "admin", Name = "Admin", Role = UserRole.Admin });
            db.EqModels.Add(new EqModel { Id = 1, Name = "Camera", Access = EqAccess.User });
            db.EqItems.AddRange(new EqItem { Id = 1, EqModelId = 1, InventoryNumber = "CAM-1" },
                new EqItem { Id = 2, EqModelId = 1, InventoryNumber = "CAM-2" });
            await db.SaveChangesAsync();
            return new Fixture(connection, db, locks);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<User>().Property(user => user.NormalizedLogin)
                .HasComputedColumnSql("lower(trim(\"Login\"))", stored: true);
        }
    }

    private sealed class LockInterceptor : DbCommandInterceptor
    {
        public List<int> ModelIds { get; } = [];
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
                return ValueTask.FromResult(result);
            if (Convert.ToInt32(command.Parameters[0].Value) == 1) ModelIds.Add(Convert.ToInt32(command.Parameters[1].Value));
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
        }
    }
}
