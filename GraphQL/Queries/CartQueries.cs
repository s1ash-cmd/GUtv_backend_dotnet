using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate.Authorization;
using Microsoft.AspNetCore.Http;

namespace GUtv_backend_dotnet.GraphQL.Queries;

[ExtendObjectType(typeof(Query))]
public class CartQueries
{
    [Authorize]
    public Task<Cart> MyCart(
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        CartService cartService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return cartService.GetOrCreateCartAsync(userId);
    }
}
