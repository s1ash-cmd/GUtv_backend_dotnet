using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Services.Telegram.Commands;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace GUtv_backend_dotnet.Services.Telegram;

public class AnnouncementDeliveryWorker(
    IServiceScopeFactory scopeFactory, ITelegramBotClient bot,
    ILogger<AnnouncementDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(5);
            try
            {
                delay = await DeliverNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Announcement delivery queue failed"); }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    private async Task<TimeSpan> DeliverNextAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Coordinate workers across application instances without sending the same row concurrently.
        var locked = await db.Database.SqlQueryRaw<bool>(
            "SELECT pg_try_advisory_xact_lock(5, 0) AS \"Value\"").SingleAsync(ct);
        if (!locked) return TimeSpan.FromSeconds(5);

        var now = DateTime.UtcNow;
        var delivery = await db.AnnouncementDeliveries.Include(d => d.Announcement)
            .Where(d => d.SentAt == null && !d.Failed && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt).ThenBy(d => d.AnnouncementId)
            .FirstOrDefaultAsync(ct);
        if (delivery is null) return TimeSpan.FromSeconds(5);

        var delay = TimeSpan.FromMilliseconds(100);
        if (!await db.Users.AnyAsync(u => !u.Banned && u.TelegramChatId == delivery.ChatId, ct))
        {
            delivery.Failed = true;
            delivery.LastError = "Аккаунт отвязан или заблокирован";
        }
        else
        {
            delivery.Attempts++;
            try
            {
                var a = delivery.Announcement;
                var message = TelegramText.AnnouncementMessage(a.AuthorName, a.Title, a.Body);
                await bot.SendMessage(delivery.ChatId,
                    message.Text,
                    entities: message.Entities,
                    cancellationToken: ct);
                delivery.SentAt = DateTime.UtcNow;
                delivery.LastError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                delivery.LastError = ex.Message[..Math.Min(ex.Message.Length, 1000)];
                if (ex is ApiRequestException { ErrorCode: 429 } limited)
                {
                    delay = TimeSpan.FromSeconds(Math.Max(1, limited.Parameters?.RetryAfter ?? 30));
                    delivery.NextAttemptAt = DateTime.UtcNow.Add(delay);
                    // Telegram's flood limit applies to the whole bot, not just one recipient.
                    await db.AnnouncementDeliveries
                        .Where(d => d.SentAt == null && !d.Failed && d.NextAttemptAt < delivery.NextAttemptAt)
                        .ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, delivery.NextAttemptAt), ct);
                }
                else
                {
                    delivery.Failed = ex is ApiRequestException { ErrorCode: 400 or 403 };
                    delivery.NextAttemptAt = DateTime.UtcNow.AddSeconds(
                        Math.Min(3600, 30 * Math.Pow(2, Math.Min(delivery.Attempts, 7))));
                }
                logger.LogWarning(ex, "Announcement {Id} delivery failed for chat {ChatId}",
                    delivery.AnnouncementId, delivery.ChatId);
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return delay;
    }
}
