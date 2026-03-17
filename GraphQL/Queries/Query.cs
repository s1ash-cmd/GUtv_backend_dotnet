using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using HotChocolate.Authorization;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.GraphQL.Queries;

public class Query
{
    // [Authorize]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<User> GetUsers(AppDbContext db) => db.Users;

}