using System.Text.Json;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using HotChocolate;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.Services;

public class EventService(AppDbContext db)
{
    public async Task<Event> CreateEventAsync(CreateEventInput input)
    {
        ValidateEventInput(input);

        var warnings = new Dictionary<string, object>();
        if ((input.StartTime - DateTime.UtcNow).TotalDays < 2)
            warnings["invalidDate"] = "Событие создается меньше чем за 3 дня";

        var entity = new Event
        {
            Client = input.Client.Trim(),
            Reason = input.Reason.Trim(),
            CreationTime = DateTime.UtcNow,
            Status = BookingStatus.Pending,
            StartTime = input.StartTime,
            EndTime = input.EndTime,
            Comment = input.Comment,
            WarningsJson = JsonSerializer.Serialize(warnings)
        };

        db.Events.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    public async Task<List<Event>> GetAllEventsAsync() =>
        await db.Events.AsNoTracking().ToListAsync();

    public async Task<Event> GetEventByIdAsync(int id)
    {
        if (id <= 0)
            throw new GraphQLException("Некорректный ID");

        return await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id)
            ?? throw new GraphQLException($"Событие с ID {id} не найдено");
    }

    public async Task<List<Event>> GetEventsByStatusAsync(BookingStatus status)
    {
        var events = await db.Events.AsNoTracking().Where(e => e.Status == status).ToListAsync();
        if (events.Count == 0)
            throw new GraphQLException($"Нет событий со статусом {status}");
        return events;
    }

    public async Task<Event> UpdateEventAsync(int id, CreateEventInput input)
    {
        ValidateEventInput(input);

        var entity = await db.Events.FindAsync(id)
            ?? throw new GraphQLException($"Событие с ID {id} не найдено");

        entity.Client = input.Client.Trim();
        entity.Reason = input.Reason.Trim();
        entity.StartTime = input.StartTime;
        entity.EndTime = input.EndTime;
        entity.Comment = input.Comment;

        await db.SaveChangesAsync();
        return entity;
    }

    public async Task<Event> ApproveEventAsync(int id, string? adminComment)
    {
        var entity = await db.Events.FindAsync(id)
            ?? throw new GraphQLException($"Событие с ID {id} не найдено");

        entity.Status = BookingStatus.Approved;
        if (!string.IsNullOrWhiteSpace(adminComment))
            entity.AdminComment = adminComment;

        await db.SaveChangesAsync();
        return entity;
    }

    public async Task<Event> CompleteEventAsync(int id)
    {
        var entity = await db.Events.FindAsync(id)
            ?? throw new GraphQLException($"Событие с ID {id} не найдено");

        entity.Status = BookingStatus.Completed;
        await db.SaveChangesAsync();
        return entity;
    }

    public async Task<Event> CancelEventAsync(int id, string? adminComment)
    {
        var entity = await db.Events.FindAsync(id)
            ?? throw new GraphQLException($"Событие с ID {id} не найдено");

        if (entity.Status == BookingStatus.Cancelled)
            throw new GraphQLException("Это событие уже отменено");

        entity.Status = BookingStatus.Cancelled;
        if (!string.IsNullOrWhiteSpace(adminComment))
            entity.AdminComment = adminComment;

        await db.SaveChangesAsync();
        return entity;
    }

    public async Task<bool> DeleteEventAsync(int id)
    {
        var entity = await db.Events.FindAsync(id)
            ?? throw new GraphQLException($"Событие с ID {id} не найдено");

        db.Events.Remove(entity);
        await db.SaveChangesAsync();
        return true;
    }

    private static void ValidateEventInput(CreateEventInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Client))
            throw new GraphQLException("Клиент не может быть пустым");

        if (string.IsNullOrWhiteSpace(input.Reason))
            throw new GraphQLException("Причина не может быть пустой");

        if (input.StartTime >= input.EndTime)
            throw new GraphQLException("Дата начала должна быть раньше даты окончания");
    }
}

public record CreateEventInput(
    string Client,
    string Reason,
    DateTime StartTime,
    DateTime EndTime,
    string? Comment
);
