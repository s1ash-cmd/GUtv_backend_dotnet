using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate.Authorization;
using Microsoft.AspNetCore.Http;

namespace GUtv_backend_dotnet.GraphQL.Queries;

[ExtendObjectType(typeof(Query))]
public class BookingQueries
{
    [Authorize]
    public Task<Booking> GetBookingById(
        int id,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        BookingService bookingService)
    {
        var httpUser = httpContextAccessor.HttpContext?.User;
        var userId = equipmentService.GetRequiredUserId(httpUser);
        var isAdmin = httpUser?.IsInRole("Admin") ?? false;
        return bookingService.GetBookingByIdAsync(id, userId, isAdmin);
    }

    [Authorize(Roles = ["Admin"])]
    public Task<List<Booking>> GetBookingsByUser(int userId, BookingService bookingService) =>
        bookingService.GetBookingsByUserAsync(userId);

    [Authorize(Roles = ["Admin"])]
    public Task<List<Booking>> GetAllBookings(BookingService bookingService) =>
        bookingService.GetAllBookingsAsync();

    [Authorize]
    public Task<List<Booking>> GetCalendarBookings(
        DateTime? start,
        DateTime? end,
        BookingService bookingService) =>
        bookingService.GetCalendarBookingsAsync(start, end);

    [Authorize]
    public Task<List<Booking>> GetMyBookings(
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        BookingService bookingService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return bookingService.GetBookingsByUserAsync(userId);
    }

    [Authorize(Roles = ["Admin"])]
    public Task<List<Booking>> GetBookingsByEquipmentItem(int equipmentItemId, BookingService bookingService) =>
        bookingService.GetBookingsByEquipmentItemAsync(equipmentItemId);

    [Authorize(Roles = ["Admin"])]
    public Task<List<Booking>> GetBookingsByStatus(BookingStatus status, BookingService bookingService) =>
        bookingService.GetBookingsByStatusAsync(status);

    [Authorize(Roles = ["Admin"])]
    public Task<List<Booking>> GetBookingsByInventoryNumber(string inventoryNumber, BookingService bookingService) =>
        bookingService.GetBookingsByInventoryNumberAsync(inventoryNumber);

    public async Task<List<Booking>> GetBookingsByTelegramChatId(
        string botToken,
        long chatId,
        BotSecurityService botSecurityService,
        UserService userService,
        BookingService bookingService)
    {
        botSecurityService.EnsureAuthorized(botToken);

        if (chatId <= 0)
            throw new GraphQLException("Некорректный chatId");

        var user = await userService.GetByTelegramChatIdAsync(chatId)
            ?? throw new GraphQLException("Пользователь не найден. Используйте /link для привязки аккаунта.");

        return await bookingService.GetBookingsByUserAsync(user.Id);
    }
}
