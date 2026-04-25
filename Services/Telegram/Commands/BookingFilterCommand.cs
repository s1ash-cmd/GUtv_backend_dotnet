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

    public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var user = await userService.GetByTelegramChatIdAsync(message.Chat.Id);
        if (user is null)
            return;

        List<Booking> bookings;
        try
        {
            bookings = await bookingService.GetBookingsByUserAsync(user.Id);
        }
        catch (GraphQLException)
        {
            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: "📭 У вас пока нет бронирований",
                cancellationToken: cancellationToken);
            return;
        }

        if (status is not null)
            bookings = bookings.Where(b => b.Status == status).ToList();

        if (bookings.Count == 0)
        {
            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: $"📭 Нет бронирований в категории <b>{TelegramText.Escape(Name)}</b>",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            return;
        }

        var response = new StringBuilder();
        response.AppendLine($"📆 <b>{TelegramText.Escape(Name)}</b>");
        response.AppendLine();

        foreach (var booking in bookings.OrderByDescending(b => b.StartTime))
        {
            response.AppendLine($"{TelegramText.GetStatusEmoji(booking.Status)} {TelegramText.BookingTitle(booking.Id)}");
            response.AppendLine($"<b>Статус:</b> {TelegramText.GetStatusName(booking.Status)}");
            response.AppendLine($"<b>Период:</b> {TelegramText.Period(booking.StartTime, booking.EndTime)}");
            response.AppendLine($"<b>Причина:</b> {TelegramText.Escape(booking.Reason)}");

            if (booking.BookingItems.Count > 0)
            {
                response.AppendLine("<b>Оборудование:</b>");
                foreach (var item in booking.BookingItems)
                    response.AppendLine($"• {TelegramText.Escape(item.EqItem.EqModel.Name)} ({TelegramText.Escape(item.EqItem.InventoryNumber)})");
            }

            if (!string.IsNullOrWhiteSpace(booking.Comment))
                response.AppendLine($"<b>Комментарий:</b> {TelegramText.Escape(booking.Comment)}");

            if (!string.IsNullOrWhiteSpace(booking.AdminComment))
                response.AppendLine($"<b>Админ:</b> {TelegramText.Escape(booking.AdminComment)}");

            response.AppendLine();
        }

        var text = response.ToString();
        if (text.Length > 4000)
            text = text[..4000] + "\n\n… показаны первые бронирования";

        await botClient.SendMessage(
            chatId: message.Chat.Id,
            text: text,
            parseMode: ParseMode.Html,
            cancellationToken: cancellationToken);
    }
}
