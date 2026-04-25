using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace GUtv_backend_dotnet.Services.Telegram.Commands;

public class ProfileCommand(UserService userService) : ICommand
{
    public string Name => "👤 Профиль";

    public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var user = await userService.GetByTelegramChatIdAsync(message.Chat.Id);
        if (user is null)
        {
            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: "⚠️ Пользователь не найден.\nИспользуйте <code>/link КОД</code> для привязки аккаунта.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            return;
        }

        var response = new StringBuilder();
        response.AppendLine("👤 <b>Ваш профиль</b>");
        response.AppendLine();
        response.AppendLine($"<b>Имя:</b> {TelegramText.Code(user.Name)}");
        response.AppendLine($"<b>Логин:</b> {TelegramText.Code(user.Login)}");
        response.AppendLine($"<b>Роль:</b> {TelegramText.Code(TelegramText.GetRole(user.Role))}");
        response.AppendLine($"<b>Ronin:</b> {TelegramText.Code(TelegramText.HasRoninAccess(user.Role))}");

        if (user.Banned)
            response.AppendLine("\n🚫 <b>Аккаунт заблокирован</b>");

        await botClient.SendMessage(
            chatId: message.Chat.Id,
            text: response.ToString(),
            parseMode: ParseMode.Html,
            cancellationToken: cancellationToken);
    }
}
