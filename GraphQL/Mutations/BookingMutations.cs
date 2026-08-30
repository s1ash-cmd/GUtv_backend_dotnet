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

    [Authorize]
    public Task<Booking> UpdateBookingFromCart(
        int bookingId,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        CartService cartService)
    {
        var httpUser = httpContextAccessor.HttpContext?.User;
        var userId = equipmentService.GetRequiredUserId(httpUser);
        var isAdmin = httpUser?.IsInRole("Admin") ?? false;
        return cartService.UpdateBookingFromCartAsync(userId, bookingId, isAdmin);
    }

    [Authorize(Roles = ["Admin"])]
    public async Task<Booking> ApproveBooking(
        int bookingId,
        string? adminComment,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        UserService userService,
        BookingService bookingService)
    {
        var admin = await GetCurrentUserAsync(httpContextAccessor, equipmentService, userService);
        return await bookingService.ApproveBookingAsync(
            bookingId,
            FormatAdminComment(admin, adminComment));
    }

    [Authorize(Roles = ["Admin"])]
    public async Task<Booking> RejectBooking(
        int bookingId,
        string? adminComment,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        UserService userService,
        BookingService bookingService)
    {
        var admin = await GetCurrentUserAsync(httpContextAccessor, equipmentService, userService);
        return await bookingService.CancelBookingAsync(
            bookingId,
            admin.Id,
            true,
            FormatAdminComment(admin, adminComment));
    }

    [Authorize(Roles = ["Admin"])]
    public Task<Booking> CompleteBooking(int id, BookingService bookingService) =>
        bookingService.CompleteBookingAsync(id);

    [Authorize]
    public async Task<Booking> CancelBooking(
        int id,
        string? adminComment,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        UserService userService,
        BookingService bookingService)
    {
        var httpUser = httpContextAccessor.HttpContext?.User;
        var userId = equipmentService.GetRequiredUserId(httpUser);
        var isAdmin = httpUser?.IsInRole("Admin") ?? false;
        var admin = isAdmin
            ? await userService.GetByIdAsync(userId)
                ?? throw new GraphQLException("Пользователь не найден")
            : null;

        return await bookingService.CancelBookingAsync(
            id,
            userId,
            isAdmin,
            isAdmin && admin is not null
                ? FormatAdminComment(admin, adminComment)
                : adminComment);
    }

    public async Task<Booking> ApproveBookingByTelegram(
        string botToken,
        long chatId,
        int bookingId,
        string? adminComment,
        BotSecurityService botSecurityService,
        UserService userService,
        BookingService bookingService)
    {
        botSecurityService.EnsureAuthorized(botToken);

        var admin = await userService.GetByTelegramChatIdAsync(chatId)
            ?? throw new GraphQLException("Пользователь не найден. Используйте /link для привязки аккаунта.");

        if (admin.Role != UserRole.Admin)
            throw new GraphQLException("У вас нет прав для этого действия");

        return await bookingService.ApproveBookingAsync(
            bookingId,
            FormatAdminComment(admin, adminComment));
    }

    public async Task<Booking> RejectBookingByTelegram(
        string botToken,
        long chatId,
        int bookingId,
        string? adminComment,
        BotSecurityService botSecurityService,
        UserService userService,
        BookingService bookingService)
    {
        botSecurityService.EnsureAuthorized(botToken);

        var admin = await userService.GetByTelegramChatIdAsync(chatId)
            ?? throw new GraphQLException("Пользователь не найден. Используйте /link для привязки аккаунта.");

        if (admin.Role != UserRole.Admin)
            throw new GraphQLException("У вас нет прав для этого действия");

        return await bookingService.CancelBookingAsync(
            bookingId,
            admin.Id,
            true,
            FormatAdminComment(admin, adminComment));
    }

    private static string? FormatAdminComment(User admin, string? comment)
    {
        return string.IsNullOrWhiteSpace(comment)
            ? admin.Name
            : $"{admin.Name}: {comment.Trim()}";
    }

    private static async Task<User> GetCurrentUserAsync(
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        UserService userService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return await userService.GetByIdAsync(userId)
            ?? throw new GraphQLException("Пользователь не найден");
    }
}
