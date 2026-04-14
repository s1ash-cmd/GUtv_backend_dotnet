using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate;
using HotChocolate.Authorization;
using Microsoft.AspNetCore.Http;

namespace GUtv_backend_dotnet.GraphQL.Mutations;

[ExtendObjectType(typeof(Mutation))]
public class EventMutations
{
    [Authorize]
    public Task<Event> CreateEvent(
        CreateEventInput input,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        EventService eventService)
    {
        var user = httpContextAccessor.HttpContext?.User;
        var canCreateEvent =
            user?.IsInRole(nameof(UserRole.Admin)) == true ||
            user?.IsInRole(nameof(UserRole.Organization)) == true;

        if (!canCreateEvent)
            throw new GraphQLException("Заявки на event доступны только представителям организаций");

        var userId = equipmentService.GetRequiredUserId(user);
        return eventService.CreateEventAsync(input, userId);
    }

    [Authorize(Roles = ["Admin"])]
    public Task<Event> UpdateEvent(int id, CreateEventInput input, EventService eventService) =>
        eventService.UpdateEventAsync(id, input);

    [Authorize(Roles = ["Admin"])]
    public Task<Event> ApproveEvent(int id, string? adminComment, EventService eventService) =>
        eventService.ApproveEventAsync(id, adminComment);

    [Authorize(Roles = ["Admin"])]
    public Task<Event> CompleteEvent(int id, EventService eventService) =>
        eventService.CompleteEventAsync(id);

    [Authorize(Roles = ["Admin"])]
    public Task<Event> CancelEvent(int id, string? adminComment, EventService eventService) =>
        eventService.CancelEventAsync(id, adminComment);

    [Authorize(Roles = ["Admin"])]
    public Task<bool> DeleteEvent(int id, EventService eventService) =>
        eventService.DeleteEventAsync(id);
}
