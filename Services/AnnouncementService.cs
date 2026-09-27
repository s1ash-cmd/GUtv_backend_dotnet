using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.Services;

public class AnnouncementService(AppDbContext db)
{
    public const int MaxTitleLength = 100;
    public const int MaxBodyLength = 3000;

    public static void Validate(string title, string body)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > MaxTitleLength)
            throw new GraphQLException("Заголовок должен содержать от 1 до 100 символов");
        if (string.IsNullOrWhiteSpace(body) || body.Trim().Length > MaxBodyLength)
            throw new GraphQLException("Текст должен содержать от 1 до 3000 символов");
    }

    public async Task<List<Announcement>> GetAsync(int? beforeId, int take, CancellationToken ct)
    {
        return await db.Announcements.AsNoTracking()
            .Where(a => beforeId == null || a.Id < beforeId)
            .OrderByDescending(a => a.Id)
            .Take(Math.Clamp(take, 1, 50))
            .ToListAsync(ct);
    }

    public async Task<Announcement> PublishAsync(
        int authorId, Guid requestId, string title, string body, CancellationToken ct)
    {
        Validate(title, body);
        if (requestId == Guid.Empty)
            throw new GraphQLException("Не указан идентификатор публикации");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serializes duplicate submissions, including a repeated Telegram callback.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(6, 0)", ct);
        var author = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == authorId, ct);
        if (author is null || author.Banned || author.Role != UserRole.Admin)
            throw new GraphQLException("Публиковать объявления могут только действующие администраторы");

        var existing = await db.Announcements.SingleOrDefaultAsync(a => a.RequestId == requestId, ct);
        if (existing is not null)
        {
            if (existing.AuthorId != authorId || existing.Title != title.Trim() || existing.Body != body.Trim())
                throw new GraphQLException("Этот идентификатор уже использован для другой публикации");
            await transaction.CommitAsync(ct);
            return existing;
        }

        var announcement = new Announcement
        {
            Title = title.Trim(), Body = body.Trim(), AuthorId = authorId,
            AuthorName = author.Name, CreatedAt = DateTime.UtcNow, RequestId = requestId
        };
        db.Announcements.Add(announcement);
        var chats = await db.Users.AsNoTracking()
            .Where(u => !u.Banned && u.TelegramChatId > 0)
            .Select(u => u.TelegramChatId!.Value).Distinct().ToListAsync(ct);
        db.AnnouncementDeliveries.AddRange(chats.Select(chat => new AnnouncementDelivery
        {
            Announcement = announcement, ChatId = chat, NextAttemptAt = announcement.CreatedAt
        }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return announcement;
    }

    public async Task<bool> DeleteAsync(int announcementId, int adminId, CancellationToken ct)
    {
        if (announcementId <= 0)
            throw new GraphQLException("Некорректный идентификатор объявления");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Wait for an in-progress delivery before removing its announcement and queued messages.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(5, 0)", ct);

        var admin = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == adminId, ct);
        if (admin is null || admin.Banned || admin.Role != UserRole.Admin)
            throw new GraphQLException("Удалять объявления могут только действующие администраторы");

        var announcement = await db.Announcements.SingleOrDefaultAsync(a => a.Id == announcementId, ct);
        if (announcement is null)
            return false;

        db.Announcements.Remove(announcement);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
