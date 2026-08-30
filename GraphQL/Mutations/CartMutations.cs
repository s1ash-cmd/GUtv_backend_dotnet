using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate.Authorization;
using Microsoft.AspNetCore.Http;

namespace GUtv_backend_dotnet.GraphQL.Mutations;

[ExtendObjectType(typeof(Mutation))]
public class CartMutations
{
    [Authorize]
    public Task<Cart> SetCartDetails(
        UpdateCartDetailsInput input,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        CartService cartService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return cartService.SetCartDetailsAsync(userId, input);
    }

    [Authorize]
    public Task<Cart> AddCartItem(
        int eqModelId,
        int quantity,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        CartService cartService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return cartService.AddCartItemAsync(userId, eqModelId, quantity);
    }

    [Authorize]
    public Task<Cart> UpdateCartItemQuantity(
        int eqModelId,
        int quantity,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        CartService cartService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return cartService.UpdateCartItemQuantityAsync(userId, eqModelId, quantity);
    }

    [Authorize]
    public Task<Cart> RemoveCartItem(
        int eqModelId,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        CartService cartService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return cartService.RemoveCartItemAsync(userId, eqModelId);
    }

    [Authorize]
    public Task<bool> ClearCart(
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        CartService cartService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return cartService.ClearCartAsync(userId);
    }

    [Authorize]
    public Task<Cart> AddBookingItemsToCart(
        int bookingId,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        CartService cartService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return cartService.AddBookingItemsToCartAsync(userId, bookingId);
    }

    [Authorize]
    public Task<Cart> PrepareBookingEdit(
        int bookingId,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        CartService cartService)
    {
        var httpUser = httpContextAccessor.HttpContext?.User;
        var userId = equipmentService.GetRequiredUserId(httpUser);
        var isAdmin = httpUser?.IsInRole("Admin") ?? false;
        return cartService.PrepareBookingEditAsync(userId, bookingId, isAdmin);
    }
}
