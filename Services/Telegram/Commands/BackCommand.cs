using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace GUtv_backend_dotnet.Services.Telegram.Commands;

public class BackCommand(UserService userService) : ICommand
{
    public string Name => "« Назад в меню";

    public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var user = await userService.GetByTelegramChatIdAsync(message.Chat.Id);
        if (user is null)
        {
            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: "⚠️ Пользователь не найден",
                cancellationToken: cancellationToken);
            return;
        }

        await botClient.SendMessage(
            chatId: message.Chat.Id,
            text: "🏠 <b>Главное меню</b>\n\nВыберите действие:",
            parseMode: ParseMode.Html,
            replyMarkup: TelegramKeyboards.MainMenu,
            cancellationToken: cancellationToken);
    }
}
