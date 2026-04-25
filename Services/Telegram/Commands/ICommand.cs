using Telegram.Bot;
using Telegram.Bot.Types;

namespace GUtv_backend_dotnet.Services.Telegram.Commands;

public interface ICommand
{
    string Name { get; }
    Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken);
}
