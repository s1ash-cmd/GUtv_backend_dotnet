using System.Text;
using GUtv_backend_dotnet.Models;
using HotChocolate;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace GUtv_backend_dotnet.Services.Telegram.Commands;

public class BookingFilterCommand(
    BookingService bookingService,
    UserService userService,
    BookingStatus? status,
    string displayName) : ICommand
{
    public static readonly string[] FilterButtons =
    [
        "⏳ Ожидают",
        "✅ Одобренные",
        "🏁 Завершенные",
        "❌ Отмененные",
        "📋 Все бронирования"
    ];

    public string Name { get; } = displayName;

    public static BookingFilterCommand Create(IServiceProvider serviceProvider, string? text)
    {
        var (status, displayName) = text switch
        {
            "⏳ Ожидают" => (BookingStatus.Pending, "Ожидают подтверждения"),
            "✅ Одобренные" => (BookingStatus.Approved, "Одобренные"),
            "🏁 Завершенные" => (BookingStatus.Completed, "Завершенные"),
            "❌ Отмененные" => (BookingStatus.Cancelled, "Отмененные"),
            _ => ((BookingStatus?)null, "Все бронирования")
        };

        return new BookingFilterCommand(
            serviceProvider.GetRequiredService<BookingService>(),
            serviceProvider.GetRequiredService<UserService>(),
            status,
            displayName);
    }

    // Telegram rejects messages above 4096 characters; the rest is room for the footer.
    private const int MessageBudget = 3800;
    private const int VisibleEquipment = 5;

    public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var user = await userService.GetByTelegramChatIdAsync(message.Chat.Id);
        if (user is null)
            return;

        // The newest page is enough for a chat message; the site has the full history.
        var page = await bookingService.GetBookingsPageByUserAsync(user.Id, status: status);
        if (page.TotalCount == 0)
        {
            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: status is null
                    ? "📭 У вас пока нет бронирований"
                    : $"📭 Нет бронирований в категории <b>{TelegramText.Escape(Name)}</b>",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            return;
        }

        await botClient.SendMessage(
            chatId: message.Chat.Id,
            text: BuildMessage(Name, page.Items, page.TotalCount),
            parseMode: ParseMode.Html,
            cancellationToken: cancellationToken);
    }

    // Bookings are added whole: cutting the finished text could split an HTML tag or entity,
    // and Telegram refuses such a message.
    public static string BuildMessage(string title, IReadOnlyList<Booking> bookings, int totalCount)
    {
        var response = new StringBuilder();
        response.AppendLine($"📆 <b>{TelegramText.Escape(title)}</b>");
        response.AppendLine();

        var shown = 0;
        foreach (var booking in bookings)
        {
            var entry = BuildEntry(booking);
            if (shown > 0 && response.Length + entry.Length > MessageBudget)
                break;
            response.Append(entry);
            shown++;
        }

        if (shown < totalCount)
            response.AppendLine($"… показаны последние {shown} из {totalCount}. Остальные — на сайте.");

        return response.ToString();
    }

    private static string BuildEntry(Booking booking)
    {
        var entry = new StringBuilder();
        entry.AppendLine($"{TelegramText.GetStatusEmoji(booking.Status)} {TelegramText.BookingTitle(booking.Id)}");
        entry.AppendLine($"<b>Статус:</b> {TelegramText.GetStatusName(booking.Status)}");
        entry.AppendLine($"<b>Период:</b> {TelegramText.Period(booking.StartTime, booking.EndTime)}");
        entry.AppendLine($"<b>Причина:</b> {TelegramNotificationService.LimitHtmlText(booking.Reason, 300)}");

        if (booking.BookingItems.Count > 0)
        {
            entry.AppendLine("<b>Оборудование:</b>");
            foreach (var item in booking.BookingItems.OrderBy(item => item.Id).Take(VisibleEquipment))
                entry.AppendLine(
                    $"• {TelegramNotificationService.LimitHtmlText(item.EqItem.EqModel.Name, 64)} " +
                    $"({TelegramNotificationService.LimitHtmlText(item.EqItem.InventoryNumber, 32)})");
            if (booking.BookingItems.Count > VisibleEquipment)
                entry.AppendLine($"… и ещё {booking.BookingItems.Count - VisibleEquipment}");
        }

        if (!string.IsNullOrWhiteSpace(booking.Comment))
            entry.AppendLine($"<b>Комментарий:</b> {TelegramNotificationService.LimitHtmlText(booking.Comment, 200)}");

        if (!string.IsNullOrWhiteSpace(booking.AdminComment))
            entry.AppendLine($"<b>Админ:</b> {TelegramNotificationService.LimitHtmlText(booking.AdminComment, 200)}");

        entry.AppendLine();
        return entry.ToString();
    }
}
