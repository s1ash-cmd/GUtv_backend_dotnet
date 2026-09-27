using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate.Authorization;

namespace GUtv_backend_dotnet.GraphQL.Mutations;

[ExtendObjectType(typeof(Mutation))]
public class AnnouncementMutations
{
    [Authorize(Roles = ["Admin"])]
    public Task<Announcement> PublishAnnouncement(
        string title, string body, Guid requestId,
        IHttpContextAccessor httpContextAccessor, EquipmentService equipmentService,
        AnnouncementService service, CancellationToken cancellationToken) =>
        service.PublishAsync(equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User),
            requestId, title, body, cancellationToken);
}
