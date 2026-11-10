using System.Data.Common;
using System.Security.Claims;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.GraphQL.Queries;
using GUtv_backend_dotnet.GraphQL.Types;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace GUtv_backend_dotnet.Tests;

public class BookingPaginationTests
{
    [Theory]
    [InlineData(0, 20, 1, 0)]
    [InlineData(30, 1, 1, 30)]
    [InlineData(30, 2, 1, 30)]
    [InlineData(31, 1, 1, 30)]
    [InlineData(31, 2, 2, 1)]
    [InlineData(80, 1, 1, 30)]
    [InlineData(80, 2, 2, 30)]
    [InlineData(80, 3, 3, 20)]
    [InlineData(80, int.MaxValue, 3, 20)]
    public async Task PageBoundariesAndClampingAreAppliedBeforeLoadingItems(
        int count, int requestedPage, int expectedPage, int expectedItemCount)
    {
        await using var fixture = await Fixture.CreateAsync(count);

        var result = await fixture.Service.GetAllBookingsPageAsync(requestedPage);

        Assert.Equal(count, result.TotalCount);
        Assert.Equal(expectedPage, result.Page);
        Assert.Equal(30, result.PageSize);
        Assert.Equal(expectedItemCount, result.Items.Count);
        Assert.Equal(Enumerable.Range(1, count).Reverse().Skip((expectedPage - 1) * 30).Take(30),
            result.Items.Select(b => b.Id));
        Assert.Empty(fixture.Db.ChangeTracker.Entries());
        var queries = fixture.Commands.Reads;
        Assert.Equal(2, queries.Count);
        Assert.Contains("COUNT(*)", queries[0]);
        Assert.Contains("LIMIT", queries[1]);
        Assert.Contains("OFFSET", queries[1]);
        Assert.All(result.Items, b =>
        {
            Assert.NotNull(b.User);
            Assert.Equal("Camera", Assert.Single(b.BookingItems).EqItem.EqModel.Name);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidPageFailsWithoutQueryingDatabase(int page)
    {
        await using var fixture = await Fixture.CreateAsync(31);

        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Service.GetAllBookingsPageAsync(page));

        Assert.Empty(fixture.Commands.Reads);
    }

    [Fact]
    public async Task StatusAndSearchAreCombinedBeforeCountingAndTakingPage()
    {
        await using var fixture = await Fixture.CreateAsync(80, booking =>
            booking.Reason = booking.Id <= 65 ? "NeEdLe project" : "Other project");

        var first = await fixture.Service.GetAllBookingsPageAsync(1, "  NEEDLE  ", BookingStatus.Approved);
        var second = await fixture.Service.GetAllBookingsPageAsync(2, "needle", BookingStatus.Approved);

        Assert.Equal(32, first.TotalCount);
        Assert.Equal(30, first.Items.Count);
        Assert.Equal(32, second.TotalCount);
        Assert.Equal(new[] { 4, 2 }, second.Items.Select(b => b.Id));
        Assert.All(first.Items.Concat(second.Items), booking =>
        {
            Assert.Equal(BookingStatus.Approved, booking.Status);
            Assert.Equal("NeEdLe project", booking.Reason);
        });
        Assert.Contains("WHERE", fixture.Commands.Reads[1]);
        Assert.Contains("lower", fixture.Commands.Reads[1]);
    }

    [Fact]
    public async Task MyBookingsScopeAppliesToCountAndFilteredPages()
    {
        await using var fixture = await Fixture.CreateAsync(80);
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "1")], "Bearer"))
            }
        };
        var queries = new BookingQueries();

        var page = await queries.GetMyBookingsPage(accessor, new EquipmentService(fixture.Db),
            fixture.Service, page: 2);
        var otherOwner = await queries.GetMyBookingsPage(accessor, new EquipmentService(fixture.Db),
            fixture.Service, search: "BetaLogin");

        Assert.Equal(40, page.TotalCount);
        Assert.Equal(10, page.Items.Count);
        Assert.All(page.Items, booking => Assert.Equal(1, booking.UserId));
        Assert.Equal(0, otherOwner.TotalCount);
        Assert.Equal(1, otherOwner.Page);
        Assert.Empty(otherOwner.Items);
        Assert.Empty(fixture.Db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("FIRST person", 40)]
    [InlineData("BETALOGIN", 40)]
    [InlineData("GENERAL BOOKING", 80)]
    [InlineData("CAMERA", 80)]
    [InlineData("1", 1)]
    [InlineData("does not exist", 0)]
    [InlineData("   ", 80)]
    public async Task SearchMatchesEachSupportedFieldCaseInsensitively(string search, int expectedCount)
    {
        await using var fixture = await Fixture.CreateAsync(80);

        var result = await fixture.Service.GetAllBookingsPageAsync(search: search);

        Assert.Equal(expectedCount, result.TotalCount);
        if (search == "1")
            Assert.Equal(1, Assert.Single(result.Items).Id);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    public async Task SearchTreatsSqlWildcardCharactersAsLiteralSubstrings(string search)
    {
        await using var fixture = await Fixture.CreateAsync(80, booking =>
            booking.Reason = booking.Id == 1 ? "Project 100%_done" : "Other project");

        var result = await fixture.Service.GetAllBookingsPageAsync(search: search);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(1, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task SortingUsesCreationTimeThenIdInBothDirectionsWithoutDuplicates()
    {
        await using var fixture = await Fixture.CreateAsync(80, booking =>
            booking.CreationTime = new DateTime(2026, 10, 1).AddDays(booking.Id % 3));
        var stored = await fixture.Db.Bookings.AsNoTracking().ToListAsync();

        foreach (var oldestFirst in new[] { false, true })
        {
            var pages = new List<Booking>();
            for (var page = 1; page <= 3; page++)
                pages.AddRange((await fixture.Service.GetAllBookingsPageAsync(page, oldestFirst: oldestFirst)).Items);
            var expected = oldestFirst
                ? stored.OrderBy(b => b.CreationTime).ThenBy(b => b.Id)
                : stored.OrderByDescending(b => b.CreationTime).ThenByDescending(b => b.Id);
            Assert.Equal(expected.Select(b => b.Id), pages.Select(b => b.Id));
            Assert.Equal(80, pages.Select(b => b.Id).Distinct().Count());
        }
    }

    [Fact]
    public async Task UnusedEquipmentItemHasEmptyHistory()
    {
        await using var fixture = await Fixture.CreateAsync(0);
        var item = new EqItem
        {
            EqModel = new EqModel { Name = "Unused camera" }, InventoryNumber = "0-002-01"
        };
        fixture.Db.EqItems.Add(item);
        await fixture.Db.SaveChangesAsync();

        Assert.Empty(await fixture.Service.GetBookingsByEquipmentItemAsync(item.Id));
    }

    [Fact]
    public async Task EquipmentHistoryIncludesCancelledBookingsAndOnlySelectedUnit()
    {
        await using var fixture = await Fixture.CreateAsync(3, booking =>
        {
            if (booking.Id == 2) booking.Status = BookingStatus.Cancelled;
        });
        var otherBooking = await fixture.Db.Bookings.Include(b => b.BookingItems)
            .SingleAsync(b => b.Id == 3);
        Assert.Single(otherBooking.BookingItems).EqItem = new EqItem
        {
            EqModel = new EqModel { Name = "Other camera" }, InventoryNumber = "0-002-01"
        };
        await fixture.Db.SaveChangesAsync();

        var history = await fixture.Service.GetBookingsByEquipmentItemAsync(1);

        Assert.Equal(new[] { 1, 2 }, history.Select(b => b.Id).Order());
        Assert.Contains(history, b => b.Status == BookingStatus.Cancelled);
    }

    [Fact]
    public async Task EquipmentHistoryIsPagedByMostRecentUseWithSearchAndStatus()
    {
        await using var fixture = await Fixture.CreateAsync(25, booking =>
        {
            booking.StartTime = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc).AddDays(booking.Id);
            booking.EndTime = booking.StartTime.AddHours(2);
            if (booking.Id == 7) booking.Reason = "Интервью с ректором";
        });
        // A booking of another unit must not appear in this unit's history.
        var other = await fixture.Db.Bookings.Include(b => b.BookingItems).SingleAsync(b => b.Id == 25);
        Assert.Single(other.BookingItems).EqItem = new EqItem
        {
            EqModel = new EqModel { Name = "Other camera" }, InventoryNumber = "0-002-01"
        };
        await fixture.Db.SaveChangesAsync();
        var otherItemId = Assert.Single(other.BookingItems).EqItemId;
        fixture.Db.ChangeTracker.Clear();

        var first = await fixture.Service.GetBookingsPageByEquipmentItemAsync(1);
        Assert.Equal(24, first.TotalCount);
        Assert.Equal(10, first.PageSize);
        Assert.Equal(Enumerable.Range(15, 10).Reverse(), first.Items.Select(b => b.Id));

        var last = await fixture.Service.GetBookingsPageByEquipmentItemAsync(1, page: 99);
        Assert.Equal(3, last.Page);
        Assert.Equal(new[] { 4, 3, 2, 1 }, last.Items.Select(b => b.Id));

        var approved = await fixture.Service.GetBookingsPageByEquipmentItemAsync(1, status: BookingStatus.Approved);
        Assert.Equal(12, approved.TotalCount);
        Assert.All(approved.Items, b => Assert.Equal(BookingStatus.Approved, b.Status));

        Assert.Equal(7, Assert.Single((await fixture.Service.GetBookingsPageByEquipmentItemAsync(1, search: "ректор")).Items).Id);
        Assert.Equal(7, Assert.Single((await fixture.Service.GetBookingsPageByEquipmentItemAsync(1, search: " #7 ")).Items).Id);
        Assert.Equal(25, Assert.Single((await fixture.Service.GetBookingsPageByEquipmentItemAsync(otherItemId)).Items).Id);
    }

    [Fact]
    public async Task ListsKeptForOldClientsReturnOnlyTheMostRecentBookings()
    {
        await using var fixture = await Fixture.CreateAsync(BookingService.LegacyListLimit + 5, booking =>
            booking.CreationTime = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(booking.Id));

        var expected = Enumerable.Range(6, BookingService.LegacyListLimit).Reverse().ToList();
        Assert.Equal(expected, (await fixture.Service.GetAllBookingsAsync()).Select(b => b.Id));
        Assert.Equal(expected, (await fixture.Service.GetBookingsByEquipmentItemAsync(1)).Select(b => b.Id));
        // Odd bookings belong to the first owner; a shorter list is still newest first.
        Assert.Equal(Enumerable.Range(1, BookingService.LegacyListLimit + 5).Where(id => id % 2 == 1).Reverse(),
            (await fixture.Service.GetBookingsByUserAsync(1)).Select(b => b.Id));
    }

    [Fact]
    public async Task CalendarRequiresABoundedPeriod()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Service.GetCalendarBookingsAsync());
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Service.GetCalendarBookingsAsync(start, null));
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Service.GetCalendarBookingsAsync(null, start));
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Service.GetCalendarBookingsAsync(start, start));
        await Assert.ThrowsAsync<GraphQLException>(() =>
            fixture.Service.GetCalendarBookingsAsync(start, start.AddDays(BookingService.CalendarMaxDays + 1)));

        // The widest allowed period, which is far more than the six weeks a month view shows.
        await fixture.Service.GetCalendarBookingsAsync(start, start.AddDays(BookingService.CalendarMaxDays));
    }

    [Fact]
    public async Task UsersArePagedWithSearchGroupsAndCountsForEveryGroup()
    {
        await using var fixture = await Fixture.CreateAsync(0);
        fixture.Db.Users.AddRange(
            new User { Id = 10, Name = "Анна Админ", Login = "anna", Role = UserRole.Admin },
            new User { Id = 11, Name = "Борис Основа", Login = "boris", Role = UserRole.Osnova, TelegramUsername = "Boris_TG" },
            new User { Id = 12, Name = "Вера Ронин", Login = "vera", Role = UserRole.Ronin },
            new User { Id = 13, Name = "Глеб Малыш", Login = "gleb", Role = UserRole.User },
            new User { Id = 14, Name = "Дина Бан", Login = "dina", Role = UserRole.Admin, Banned = true });
        for (var id = 100; id < 100 + UserService.UsersPageSize; id++)
            fixture.Db.Users.Add(new User { Id = id, Name = $"User {id}", Login = $"login{id}" });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var users = new UserService(fixture.Db);

        var all = await users.GetUsersPageAsync();
        Assert.Equal(UserService.UsersPageSize + 5, all.TotalCount);
        Assert.Equal(UserService.UsersPageSize, all.Items.Count);
        Assert.Equal(new UserGroupCounts(UserService.UsersPageSize + 5, 1, 2, UserService.UsersPageSize + 1, 1), all.Counts);
        Assert.Equal(5, (await users.GetUsersPageAsync(page: 2)).Items.Count);
        Assert.Equal(2, (await users.GetUsersPageAsync(page: 99)).Page);

        // A banned administrator is listed as banned, not as an administrator.
        Assert.Equal(new[] { 10 }, (await users.GetUsersPageAsync(group: UserListGroup.Admins)).Items.Select(u => u.Id));
        Assert.Equal(new[] { 14 }, (await users.GetUsersPageAsync(group: UserListGroup.Banned)).Items.Select(u => u.Id));
        Assert.Equal(new[] { 11, 12 }, (await users.GetUsersPageAsync(group: UserListGroup.Osnova)).Items.Select(u => u.Id).Order());
        Assert.Equal(UserService.UsersPageSize + 1, (await users.GetUsersPageAsync(group: UserListGroup.Users)).TotalCount);

        // Search covers the login and the Telegram name, and does not change the group counts.
        var byTelegram = await users.GetUsersPageAsync(search: " @boris_tg ");
        Assert.Equal(11, Assert.Single(byTelegram.Items).Id);
        Assert.Equal(all.Counts, byTelegram.Counts);
        Assert.Equal(13, Assert.Single((await users.GetUsersPageAsync(search: "GLEB")).Items).Id);
        Assert.Empty((await users.GetUsersPageAsync(search: "gleb", group: UserListGroup.Admins)).Items);

        var ascending = (await users.GetUsersPageAsync(search: "login10")).Items.Select(u => u.Id).ToList();
        var descending = (await users.GetUsersPageAsync(search: "login10", descending: true)).Items.Select(u => u.Id).ToList();
        Assert.Equal(Enumerable.Range(100, 10), ascending);
        Assert.Equal(ascending.AsEnumerable().Reverse(), descending);
    }

    [Fact]
    public async Task GraphqlSchemaHasFixedPagePayloadDefaultsAndCorrectAuthorization()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddAuthorization();
        services.AddScoped<BookingService>();
        services.AddScoped<EquipmentService>();
        services.AddScoped<UserService>();
        services.AddSingleton<RequestRateLimiter>();
        services.AddGraphQLServer()
            .AddQueryType<Query>()
            .AddTypeExtension<BookingQueries>()
            .AddType<BookingStatusType>()
            .AddAuthorization();
        await using var provider = services.BuildServiceProvider();
        var executor = await provider.GetRequiredService<IRequestExecutorResolver>().GetRequestExecutorAsync();
        var all = executor.Schema.QueryType.Fields["allBookingsPage"];
        var mine = executor.Schema.QueryType.Fields["myBookingsPage"];
        var byUser = executor.Schema.QueryType.Fields["bookingsPageByUser"];
        Assert.Equal("Int!", FormatType(byUser.Arguments["userId"].Type));
        Assert.Equal("1", byUser.Arguments["page"].DefaultValue!.ToString());
        var byUserAuth = Assert.Single(byUser.Directives, d => d.Type.Name == "authorize").AsValue<AuthorizeDirective>();
        Assert.Equal(new[] { "Admin" }, byUserAuth.Roles);

        foreach (var field in new[] { all, mine })
        {
            Assert.Equal(4, field.Arguments.Count);
            Assert.Equal("Int!", FormatType(field.Arguments["page"].Type));
            Assert.Equal("1", field.Arguments["page"].DefaultValue!.ToString());
            Assert.Equal("String", FormatType(field.Arguments["search"].Type));
            Assert.Equal("BookingStatus", FormatType(field.Arguments["status"].Type));
            Assert.Equal("Boolean!", FormatType(field.Arguments["oldestFirst"].Type));
            Assert.Equal("false", field.Arguments["oldestFirst"].DefaultValue!.ToString());
        }
        var payload = Assert.IsAssignableFrom<ObjectType>(all.Type.NamedType());
        Assert.Equal("[Booking!]!", FormatType(payload.Fields["items"].Type));
        foreach (var name in new[] { "totalCount", "page", "pageSize" })
            Assert.Equal("Int!", FormatType(payload.Fields[name].Type));
        var allAuth = Assert.Single(all.Directives, d => d.Type.Name == "authorize").AsValue<AuthorizeDirective>();
        Assert.Equal(new[] { "Admin" }, allAuth.Roles);
        var mineAuth = Assert.Single(mine.Directives, d => d.Type.Name == "authorize").AsValue<AuthorizeDirective>();
        Assert.True(mineAuth.Roles is null or { Count: 0 });
        // The unpaginated lists stay for old clients, marked as deprecated.
        foreach (var name in new[] { "allBookings", "myBookings", "bookingsByUser", "bookingsByEquipmentItem" })
            Assert.True(executor.Schema.QueryType.Fields[name].IsDeprecated, name);

        var history = executor.Schema.QueryType.Fields["bookingsPageByEquipmentItem"];
        Assert.Equal("Int!", FormatType(history.Arguments["equipmentItemId"].Type));
        Assert.Equal("1", history.Arguments["page"].DefaultValue!.ToString());
        Assert.Equal("String", FormatType(history.Arguments["search"].Type));
        Assert.Equal("BookingStatus", FormatType(history.Arguments["status"].Type));
        Assert.Same(payload, history.Type.NamedType());
        var historyAuth = Assert.Single(history.Directives, d => d.Type.Name == "authorize").AsValue<AuthorizeDirective>();
        Assert.Equal(new[] { "Admin" }, historyAuth.Roles);
    }

    private static string FormatType(IType type) => type switch
    {
        NonNullType nonNull => FormatType(nonNull.Type) + "!",
        ListType list => $"[{FormatType(list.ElementType)}]",
        INamedType named => named.Name,
        _ => throw new ArgumentException("Unknown GraphQL type", nameof(type))
    };

    private sealed class Fixture(SqliteConnection connection, AppDbContext db, SqlCapture commands) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public SqlCapture Commands { get; } = commands;
        public BookingService Service => new(Db, null!);

        public static async Task<Fixture> CreateAsync(int count, Action<Booking>? customize = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var commands = new SqlCapture();
            var db = new PaginationDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection).AddInterceptors(commands).Options);
            await db.Database.EnsureCreatedAsync();
            var firstOwner = new User { Id = 1, Name = "First Person", Login = "AlphaLogin" };
            var secondOwner = new User { Id = 2, Name = "Second Person", Login = "BetaLogin" };
            var equipment = new EqItem
            {
                EqModel = new EqModel { Name = "Camera" }, InventoryNumber = "0-001-01"
            };
            for (var id = 1; id <= count; id++)
            {
                var booking = new Booking
                {
                    Id = id,
                    User = id % 2 == 1 ? firstOwner : secondOwner,
                    Reason = "General booking",
                    Status = id % 2 == 1 ? BookingStatus.Pending : BookingStatus.Approved,
                    CreationTime = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
                    BookingItems = [new BookingItem { EqItem = equipment }]
                };
                customize?.Invoke(booking);
                db.Bookings.Add(booking);
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            commands.Reads.Clear();
            return new Fixture(connection, db, commands);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class PaginationDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<User>().Property(user => user.NormalizedLogin)
                .HasComputedColumnSql("lower(trim(\"Login\"))", stored: true);
        }
    }

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public List<string> Reads { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT")) Reads.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
