using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.GraphQL.Queries;
using GUtv_backend_dotnet.GraphQL.Types;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GUtv_backend_dotnet.Tests;

public class CalendarPrivacyTests
{
    [Fact]
    public async Task Calendar_projects_active_bookings_and_contact_without_loading_private_entities()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var user = new User
        {
            Id = 1, Name = "Участник", Login = "private-login", PasswordHash = "private-hash",
            TelegramChatId = 123456, TelegramUsername = "public-contact",
            RefreshToken = "private-refresh", Role = UserRole.Admin
        };
        var model = new EqModel { Id = 1, Name = "Камера" };
        var item = new EqItem { Id = 1, EqModel = model, InventoryNumber = "0-001-01" };
        var start = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        foreach (var status in Enum.GetValues<BookingStatus>())
        {
            db.Bookings.Add(new Booking
            {
                Id = (int)status + 1, User = user, Reason = "Съёмка", Status = status,
                StartTime = start, EndTime = start.AddHours(2),
                Comment = "private-comment", AdminComment = "private-admin-comment",
                BookingItems = [new BookingItem
                {
                    EqItem = item, StartDate = start, EndDate = start.AddHours(2)
                }]
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Calendar queries do not send notifications, so no Telegram client is needed.
        var service = new BookingService(db, null!);
        var bookings = await service.GetCalendarBookingsAsync(start.AddMinutes(30), start.AddHours(1));

        Assert.Equal(2, bookings.Count);
        Assert.All(bookings, booking =>
        {
            Assert.Equal("Участник", booking.UserName);
            Assert.Equal("public-contact", booking.TelegramUsername);
            Assert.Equal("Съёмка", booking.Reason);
            var equipment = Assert.Single(booking.Equipment);
            Assert.Equal("Камера", equipment.ModelName);
            Assert.Equal("0-001-01", equipment.InventoryNumber);
        });
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Empty(await service.GetCalendarBookingsAsync(start.AddHours(2), start.AddHours(3)));
    }

    [Fact]
    public async Task Graphql_calendar_rejects_private_fields_and_entity_navigation()
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
        var calendarType = Assert.IsAssignableFrom<ObjectType>(executor.Schema.QueryType.Fields["calendarBookings"].Type.NamedType());

        foreach (var field in new[] { "comment", "adminComment", "warningsJson", "login", "user", "userId", "bookingItems" })
            Assert.DoesNotContain(calendarType.Fields, actual => actual.Name == field);
        var equipmentType = Assert.IsAssignableFrom<ObjectType>(calendarType.Fields["equipment"].Type.NamedType());
        foreach (var field in new[] { "booking", "eqItem", "eqItemId", "isReturned" })
            Assert.DoesNotContain(equipmentType.Fields, actual => actual.Name == field);

        await using var result = await executor.ExecuteAsync("""
            { calendarBookings { comment adminComment user { telegramChatId role banned } } }
            """);
        var operation = Assert.IsAssignableFrom<IOperationResult>(result);
        Assert.NotEmpty(operation.Errors!);
        Assert.Contains(operation.Errors!, error => error.Message.Contains("comment"));
        Assert.Contains(operation.Errors!, error => error.Message.Contains("adminComment"));
        Assert.Contains(operation.Errors!, error => error.Message.Contains("user"));
    }
}
