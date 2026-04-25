using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace GUtv_backend_dotnet.Services.Telegram.Commands;

public class HelpCommand : ICommand
{
    public string Name => "ℹ️ Помощь";

    public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var response = new StringBuilder();
        response.AppendLine("ℹ️ <b>GUtv Booker</b>");
        response.AppendLine();
        response.AppendLine("<b>Команды:</b>");
        response.AppendLine("<code>/start</code> - главное меню");
        response.AppendLine("<code>/link КОД</code> - привязать аккаунт");
        response.AppendLine();
        response.AppendLine("Если меню потерялось, введите <code>/start</code>.");

        await botClient.SendMessage(
            chatId: message.Chat.Id,
            text: response.ToString(),
            parseMode: ParseMode.Html,
            cancellationToken: cancellationToken);
    }
}
