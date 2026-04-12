using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate.Authorization;

namespace GUtv_backend_dotnet.GraphQL.Queries;

[ExtendObjectType(typeof(Query))]
public class EventQueries
{
    [Authorize]
    public Task<List<Event>> GetAllEvents(EventService eventService) =>
        eventService.GetAllEventsAsync();

    [Authorize]
    public Task<Event> GetEventById(int id, EventService eventService) =>
        eventService.GetEventByIdAsync(id);

    [Authorize]
    public Task<List<Event>> GetEventsByStatus(BookingStatus status, EventService eventService) =>
        eventService.GetEventsByStatusAsync(status);
}
