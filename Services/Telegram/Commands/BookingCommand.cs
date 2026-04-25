using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace GUtv_backend_dotnet.Services.Telegram.Commands;

public class BookingCommand(UserService userService) : ICommand
{
    public string Name => "📆 Мои бронирования";

    public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var user = await userService.GetByTelegramChatIdAsync(message.Chat.Id);
        if (user is null)
        {
            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: "⚠️ Пользователь не зарегистрирован.\nИспользуйте <code>/link КОД</code> для привязки аккаунта.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            return;
        }

        var keyboard = new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { "⏳ Ожидают", "✅ Одобренные" },
            new KeyboardButton[] { "🏁 Завершенные", "❌ Отмененные" },
            new KeyboardButton[] { "📋 Все бронирования" },
            new KeyboardButton[] { "« Назад в меню" }
        })
        {
            ResizeKeyboard = true
        };

        await botClient.SendMessage(
            chatId: message.Chat.Id,
            text: "📆 <b>Мои бронирования</b>\n\nВыберите категорию:",
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
    }
}
