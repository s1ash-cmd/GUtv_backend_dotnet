using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate.Authorization;
using Microsoft.AspNetCore.Http;

namespace GUtv_backend_dotnet.GraphQL.Queries;

[ExtendObjectType(typeof(Query))]
public class EventQueries
{
    [Authorize(Roles = ["Admin"])]
    public Task<List<Event>> GetAllEvents(EventService eventService) =>
        eventService.GetAllEventsAsync();

    [Authorize]
    public Task<Event> GetEventById(
        int id,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        EventService eventService)
    {
        var httpUser = httpContextAccessor.HttpContext?.User;
        var userId = equipmentService.GetRequiredUserId(httpUser);
        var isAdmin = httpUser?.IsInRole(nameof(UserRole.Admin)) ?? false;
        return eventService.GetEventByIdForUserAsync(id, userId, isAdmin);
    }

    [Authorize]
    public Task<List<Event>> GetMyEvents(
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        EventService eventService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return eventService.GetEventsByUserAsync(userId);
    }

    [Authorize(Roles = ["Admin"])]
    public Task<List<Event>> GetEventsByStatus(BookingStatus status, EventService eventService) =>
        eventService.GetEventsByStatusAsync(status);
}
