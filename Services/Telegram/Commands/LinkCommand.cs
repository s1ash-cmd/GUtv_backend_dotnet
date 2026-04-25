using HotChocolate;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace GUtv_backend_dotnet.Services.Telegram.Commands;

public class LinkCommand(UserService userService, ILogger<LinkCommand> logger) : ICommand
{
    public string Name => "/link";

    public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var parts = message.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is null || parts.Length != 2)
        {
            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: "⚠️ <b>Неверный формат команды</b>\n\nИспользуйте: <code>/link КОД</code>",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            return;
        }

        var code = parts[1];
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: "⚠️ Код должен состоять из <b>6 цифр</b>.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            return;
        }

        var chatId = message.Chat.Id;
        var username = message.From?.Username;

        try
        {
            var user = await userService.LinkTelegramByCode(code, chatId, username);
            logger.LogInformation("Telegram @{Username} ({ChatId}) linked to {Login}", username, chatId, user.Login);

            await botClient.SendMessage(
                chatId: chatId,
                text: "✅ <b>Telegram успешно привязан</b>\n\n" +
                      $"👤 <b>Имя:</b> {TelegramText.Escape(user.Name)}\n" +
                      $"🔑 <b>Логин:</b> {TelegramText.Code(user.Login)}\n" +
                      $"💬 <b>Telegram:</b> @{TelegramText.Escape(username)}\n" +
                      $"🛡 <b>Роль:</b> {TelegramText.Escape(TelegramText.GetRole(user.Role))}\n\n" +
                      "Используйте /start для вызова меню.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }
        catch (GraphQLException ex)
        {
            await botClient.SendMessage(chatId, ex.Message, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Telegram link failed. ChatId: {ChatId}, Code: {Code}", chatId, code);
            await botClient.SendMessage(
                chatId: chatId,
                text: "❌ Произошла ошибка при привязке аккаунта.",
                cancellationToken: cancellationToken);
        }
    }
}
