using System.Security.Claims;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.GraphQL.Queries;

[ExtendObjectType(typeof(Query))]
public class UserQueries
{
    [Authorize(Roles = ["Admin"])]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<User> GetUsers(AppDbContext db) => db.Users;

    [Authorize]
    public async Task<User> Me(
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        AppDbContext db)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);

        return await db.Users.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new GraphQLException("Пользователь не найден");
    }

    [Authorize(Roles = ["Admin"])]
    public async Task<User> GetUserById(int id, AppDbContext db)
    {
        if (id <= 0)
            throw new GraphQLException("Некорректный ID");

        return await db.Users.FirstOrDefaultAsync(u => u.Id == id)
            ?? throw new GraphQLException("Пользователь не найден");
    }
}
