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
    public async Task GraphqlSchemaHasFixedPagePayloadDefaultsAndCorrectAuthorization()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddAuthorization();
        services.AddScoped<BookingService>();
        services.AddScoped<EquipmentService>();
        services.AddScoped<UserService>();
        services.AddScoped<BotSecurityService>();
        services.AddGraphQLServer()
            .AddQueryType<Query>()
            .AddTypeExtension<BookingQueries>()
            .AddType<BookingStatusType>()
            .AddAuthorization();
        await using var provider = services.BuildServiceProvider();
        var executor = await provider.GetRequiredService<IRequestExecutorResolver>().GetRequestExecutorAsync();
        var all = executor.Schema.QueryType.Fields["allBookingsPage"];
        var mine = executor.Schema.QueryType.Fields["myBookingsPage"];

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
        Assert.Contains(executor.Schema.QueryType.Fields, f => f.Name == "allBookings");
        Assert.Contains(executor.Schema.QueryType.Fields, f => f.Name == "myBookings");
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
