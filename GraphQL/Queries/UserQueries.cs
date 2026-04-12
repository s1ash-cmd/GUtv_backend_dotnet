using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using HotChocolate.Authorization;

namespace GUtv_backend_dotnet.GraphQL.Queries;

[ExtendObjectType(typeof(Query))]
public class UserQueries
{
    [Authorize(Roles = ["Admin"])]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<User> GetUsers(AppDbContext db) => db.Users;
}