using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate.Authorization;
using Microsoft.AspNetCore.Http;

namespace GUtv_backend_dotnet.GraphQL.Mutations;

[ExtendObjectType(typeof(Mutation))]
public class BookingMutations
{
    [Authorize]
    public Task<Booking> CreateBooking(
        CreateBookingInput input,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        BookingService bookingService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return bookingService.CreateBookingAsync(input, userId);
    }

    [Authorize]
    public Task<Booking> CreateBookingFromCart(
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        CartService cartService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return cartService.CreateBookingFromCartAsync(userId);
    }

    [Authorize(Roles = ["Admin"])]
    public Task<Booking> ApproveBooking(
        int bookingId,
        string? adminComment,
        BookingService bookingService) =>
        bookingService.ApproveBookingAsync(bookingId, adminComment);

    [Authorize(Roles = ["Admin"])]
    public Task<Booking> RejectBooking(
        int bookingId,
        string? adminComment,
        BookingService bookingService) =>
        bookingService.CancelBookingAsync(bookingId, 0, true, adminComment);

    [Authorize(Roles = ["Admin"])]
    public Task<Booking> CompleteBooking(int id, BookingService bookingService) =>
        bookingService.CompleteBookingAsync(id);

    [Authorize]
    public Task<Booking> CancelBooking(
        int id,
        string? adminComment,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        BookingService bookingService)
    {
        var httpUser = httpContextAccessor.HttpContext?.User;
        var userId = equipmentService.GetRequiredUserId(httpUser);
        var isAdmin = httpUser?.IsInRole("Admin") ?? false;
        return bookingService.CancelBookingAsync(id, userId, isAdmin, adminComment);
    }
}
