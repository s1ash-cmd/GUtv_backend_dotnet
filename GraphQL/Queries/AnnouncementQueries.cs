using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate.Authorization;

namespace GUtv_backend_dotnet.GraphQL.Queries;

[ExtendObjectType(typeof(Query))]
public class AnnouncementQueries
{
    [Authorize]
    public Task<List<Announcement>> GetAnnouncements(
        AnnouncementService service, CancellationToken cancellationToken, int? beforeId = null, int take = 20) =>
        service.GetAsync(beforeId, take, cancellationToken);
}
