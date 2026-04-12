using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate.Authorization;

namespace GUtv_backend_dotnet.GraphQL.Mutations;

[ExtendObjectType(typeof(Mutation))]
public class EventMutations
{

    public Task<Event> CreateEvent(CreateEventInput input, EventService eventService) =>
        eventService.CreateEventAsync(input);

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
