using System.Text;
using System.Text.Json;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services.Telegram.Commands;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace GUtv_backend_dotnet.Services.Telegram;

public class TelegramNotificationService(
    ITelegramBotClient botClient,
    AppDbContext db,
    ILogger<TelegramNotificationService> logger)
{
    public async Task NotifyAdminsNewBooking(Booking booking)
    {
        await NotifyAdminsBookingSubmitted(booking, isUpdated: false);
    }

    public async Task NotifyAdminsBookingUpdated(Booking booking)
    {
        await NotifyAdminsBookingSubmitted(booking, isUpdated: true);
    }

    private async Task NotifyAdminsBookingSubmitted(Booking booking, bool isUpdated)
    {
        try
        {
            var admins = await db.Users
                .Where(u => u.Role == UserRole.Admin && u.TelegramChatId.HasValue)
                .ToListAsync();

            if (admins.Count == 0)
                return;

            var loadedBooking = await LoadBooking(booking.Id);
            var message = BuildBookingSubmittedMessage(loadedBooking, isUpdated);
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Подтвердить", $"booking:approve:{loadedBooking.Id}"),
                    InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"booking:reject:{loadedBooking.Id}")
                }
            });

            foreach (var admin in admins)
            {
                await botClient.SendMessage(
                    chatId: admin.TelegramChatId!.Value,
                    text: message,
                    parseMode: ParseMode.Html,
                    replyMarkup: keyboard);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Telegram admin notification failed for booking {BookingId}", booking.Id);
        }
    }

    public async Task NotifyUserBookingStatusChanged(Booking booking, BookingStatus oldStatus)
    {
        try
        {
            var loadedBooking = await LoadBooking(booking.Id);
            if (loadedBooking.User.TelegramChatId is null)
                return;

            var message = BuildStatusChangedMessage(loadedBooking, oldStatus);
            await botClient.SendMessage(
                chatId: loadedBooking.User.TelegramChatId.Value,
                text: message,
                parseMode: ParseMode.Html);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Telegram user notification failed for booking {BookingId}", booking.Id);
        }
    }

    private async Task<Booking> LoadBooking(int bookingId)
    {
        return await db.Bookings
            .AsNoTracking()
            .Include(b => b.User)
            .Include(b => b.BookingItems)
            .ThenInclude(bi => bi.EqItem)
            .ThenInclude(i => i.EqModel)
            .FirstAsync(b => b.Id == bookingId);
    }

    private static string BuildBookingSubmittedMessage(Booking booking, bool isUpdated)
    {
        var message = new StringBuilder();
        message.AppendLine($"{(isUpdated ? "✏️" : "🆕")} {TelegramText.BookingTitle(booking.Id)}");
        message.AppendLine(isUpdated
            ? "<b>Заявка изменена и снова ожидает решения</b>"
            : "<b>Новая заявка ожидает решения</b>");
        message.AppendLine();
        message.AppendLine($"👤 <b>Пользователь:</b> {TelegramText.Escape(booking.User.Name)} (@{TelegramText.Escape(booking.User.TelegramUsername)})");
        message.AppendLine($"📝 <b>Причина:</b> {TelegramText.Escape(booking.Reason)}");
        message.AppendLine($"📅 <b>Период:</b> {TelegramText.Period(booking.StartTime, booking.EndTime)}");
        message.AppendLine();
        message.AppendLine("📦 <b>Оборудование:</b>");

        foreach (var item in booking.BookingItems)
            message.AppendLine($"• {TelegramText.Escape(item.EqItem.EqModel.Name)} ({TelegramText.Escape(item.EqItem.InventoryNumber)})");

        if (!string.IsNullOrWhiteSpace(booking.Comment))
            message.AppendLine($"\n💬 <b>Комментарий:</b> {TelegramText.Escape(booking.Comment)}");

        var warnings = ReadWarnings(booking.WarningsJson);
        if (warnings.Count > 0)
            message.AppendLine($"\n⚠️ <b>Предупреждения:</b> {TelegramText.Escape(string.Join(", ", warnings))}");

        message.AppendLine($"\n{TelegramText.GetStatusEmoji(booking.Status)} <b>Статус:</b> {TelegramText.GetStatusName(booking.Status)}");
        return message.ToString();
    }

    private static string BuildStatusChangedMessage(Booking booking, BookingStatus oldStatus)
    {
        var message = new StringBuilder();
        message.AppendLine($"{TelegramText.GetStatusEmoji(booking.Status)} {TelegramText.BookingTitle(booking.Id)}");
        message.AppendLine("<b>Статус обновлен</b>");
        message.AppendLine();
        message.AppendLine($"<s>{TelegramText.GetStatusName(oldStatus)}</s> → <b>{TelegramText.GetStatusName(booking.Status)}</b>");
        message.AppendLine();
        message.AppendLine($"📝 <b>Причина:</b> {TelegramText.Escape(booking.Reason)}");
        message.AppendLine($"📅 <b>Период:</b> {TelegramText.Period(booking.StartTime, booking.EndTime)}");
        message.AppendLine();
        message.AppendLine("📦 <b>Оборудование:</b>");

        foreach (var item in booking.BookingItems)
            message.AppendLine($"• {TelegramText.Escape(item.EqItem.EqModel.Name)} ({TelegramText.Escape(item.EqItem.InventoryNumber)})");

        if (!string.IsNullOrWhiteSpace(booking.AdminComment))
            message.AppendLine($"\n💬 <b>Комментарий администратора:</b> {TelegramText.Escape(booking.AdminComment)}");

        return message.ToString();
    }

    private static List<string> ReadWarnings(string warningsJson)
    {
        if (string.IsNullOrWhiteSpace(warningsJson))
            return [];

        try
        {
            var warnings = JsonSerializer.Deserialize<Dictionary<string, object>>(warningsJson);
            return warnings?.Values.Select(v => v.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)).Cast<string>().ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }
}
