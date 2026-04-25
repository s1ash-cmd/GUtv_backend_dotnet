using HotChocolate;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace GUtv_backend_dotnet.Services.Telegram.Commands;

public class StartCommand(UserService userService, ILogger<StartCommand> logger) : ICommand
{
    public string Name => "/start";

    public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var username = message.From?.Username;
        var parts = message.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var startParameter = parts?.Length > 1 ? parts[1] : null;

        if (!string.IsNullOrWhiteSpace(startParameter) &&
            startParameter.StartsWith("LINK_", StringComparison.OrdinalIgnoreCase))
        {
            var code = startParameter["LINK_".Length..];
            if (code.Length == 6 && code.All(char.IsDigit))
            {
                logger.LogInformation("Telegram autolink attempt. ChatId: {ChatId}, Code: {Code}", chatId, code);
                await LinkByCode(botClient, message, code, cancellationToken);
                return;
            }
        }

        var user = await userService.GetByTelegramChatIdAsync(chatId);
        if (user is null)
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: "👋 <b>GUtv Booker</b>\n\n" +
                      "Чтобы пользоваться ботом, привяжите аккаунт.\n\n" +
                      "1. Откройте личный кабинет\n" +
                      "2. Нажмите <b>Привязать Telegram</b>\n" +
                      "3. Перейдите по ссылке или отправьте сюда <code>/link КОД</code>\n\n" +
                      $"Ваш Telegram: @{TelegramText.Escape(username)}",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            return;
        }

        await botClient.SendMessage(
            chatId: chatId,
            text: $"👋 <b>Здравствуйте, {TelegramText.Escape(user.Name)}!</b>\n\nВыберите действие:",
            parseMode: ParseMode.Html,
            replyMarkup: TelegramKeyboards.MainMenu,
            cancellationToken: cancellationToken);
    }

    private async Task LinkByCode(ITelegramBotClient botClient, Message message, string code, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var username = message.From?.Username;

        try
        {
            var user = await userService.LinkTelegramByCode(code, chatId, username);
            await botClient.SendMessage(
                chatId: chatId,
                text: "✅ <b>Telegram успешно привязан</b>\n\n" +
                      $"👤 <b>Имя:</b> {TelegramText.Escape(user.Name)}\n" +
                      $"🔑 <b>Логин:</b> {TelegramText.Code(user.Login)}\n" +
                      $"💬 <b>Telegram:</b> @{TelegramText.Escape(username)}\n\n" +
                      "Теперь доступны меню, профиль и бронирования.",
                parseMode: ParseMode.Html,
                replyMarkup: TelegramKeyboards.MainMenu,
                cancellationToken: cancellationToken);
        }
        catch (GraphQLException ex)
        {
            await botClient.SendMessage(chatId, ex.Message, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Telegram autolink failed. ChatId: {ChatId}, Code: {Code}", chatId, code);
            await botClient.SendMessage(
                chatId: chatId,
                text: "Произошла ошибка при привязке аккаунта. Попробуйте вручную: /link КОД",
                cancellationToken: cancellationToken);
        }
    }
}
