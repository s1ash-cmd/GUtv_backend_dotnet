using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate.Authorization;
using Microsoft.AspNetCore.Http;

namespace GUtv_backend_dotnet.GraphQL.Queries;

[ExtendObjectType(typeof(Query))]
public class BookingQueries
{
    [Authorize]
    public Task<Booking> GetBookingById(int id, BookingService bookingService) =>
        bookingService.GetBookingByIdAsync(id);

    [Authorize(Roles = ["Admin"])]
    public Task<List<Booking>> GetBookingsByUser(int userId, BookingService bookingService) =>
        bookingService.GetBookingsByUserAsync(userId);

    [Authorize(Roles = ["Admin"])]
    public Task<List<Booking>> GetAllBookings(BookingService bookingService) =>
        bookingService.GetAllBookingsAsync();

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
}
