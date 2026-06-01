using System.Collections.Concurrent;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services.Telegram.Commands;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace GUtv_backend_dotnet.Services.Telegram;

public class TelegramUpdateHandler
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TelegramUpdateHandler> _logger;
    private readonly Dictionary<string, Type> _commands;
    private readonly ConcurrentDictionary<long, (string Action, int BookingId)> _pendingComments = new();
    private readonly string? _unknownCommandVideo;

    public TelegramUpdateHandler(
        IServiceProvider serviceProvider,
        ILogger<TelegramUpdateHandler> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _commands = RegisterCommands();
        _unknownCommandVideo = configuration["BotConfiguration:UnknownCommandVideoUrl"];
    }

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.CallbackQuery is { } callbackQuery)
        {
            await HandleCallbackQuery(botClient, callbackQuery, cancellationToken);
            return;
        }

        if (update.Message is not { } message)
            return;

        var chatId = message.Chat.Id;
        var username = message.From?.Username;
        var messageText = message.Text;

        _logger.LogInformation("Telegram message from @{Username} ({ChatId}): {Message}", username ?? "unknown", chatId, messageText ?? update.Message.Type.ToString());
        await UpdateUsername(chatId, username);

        if (_pendingComments.ContainsKey(chatId))
        {
            if (messageText is null)
            {
                await botClient.SendMessage(
                    chatId: chatId,
                    text: "💬 Напишите текстовый комментарий или <code>-</code>, чтобы пропустить.",
                    parseMode: ParseMode.Html,
                    cancellationToken: cancellationToken);
                return;
            }

            await HandleCommentReply(botClient, message, cancellationToken);
            return;
        }

        if (messageText is null)
        {
            await SendUnknownCommandResponse(botClient, chatId, cancellationToken);
            return;
        }

        var commandKey = messageText.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if ((commandKey is not null && _commands.TryGetValue(commandKey, out var commandType)) ||
            _commands.TryGetValue(messageText, out commandType))
        {
            await ExecuteCommand(commandType, botClient, message, cancellationToken);
            return;
        }

        await SendUnknownCommandResponse(botClient, chatId, cancellationToken);
    }

    private Dictionary<string, Type> RegisterCommands()
    {
        var commands = new Dictionary<string, Type>
        {
            ["/start"] = typeof(StartCommand),
            ["/link"] = typeof(LinkCommand),
            ["👤 Профиль"] = typeof(ProfileCommand),
            ["📆 Мои бронирования"] = typeof(BookingCommand),
            ["ℹ️ Помощь"] = typeof(HelpCommand),
            ["« Назад в меню"] = typeof(BackCommand)
        };

        foreach (var button in BookingFilterCommand.FilterButtons)
            commands[button] = typeof(BookingFilterCommand);

        return commands;
    }

    private async Task HandleCallbackQuery(ITelegramBotClient botClient, CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        try
        {
            var data = callbackQuery.Data;
            var chatId = callbackQuery.Message?.Chat.Id;
            if (chatId is null || data?.StartsWith("booking:", StringComparison.Ordinal) != true)
                return;

            var parts = data.Split(':');
            if (parts.Length != 3 || !int.TryParse(parts[2], out var bookingId))
                return;

            using var scope = _serviceProvider.CreateScope();
            var userService = scope.ServiceProvider.GetRequiredService<UserService>();
            var admin = await userService.GetByTelegramChatIdAsync(chatId.Value);

            if (admin?.Role != UserRole.Admin)
            {
                await botClient.AnswerCallbackQuery(
                    callbackQuery.Id,
                    "У вас нет прав для этого действия",
                    showAlert: true,
                    cancellationToken: cancellationToken);
                return;
            }

            var action = parts[1];
            _pendingComments[chatId.Value] = (action, bookingId);
            var actionText = action == "approve" ? "одобрения" : "отклонения";

            await botClient.SendMessage(
                chatId: chatId.Value,
                text: $"💬 <b>Комментарий для {actionText}</b>\n\n{TelegramText.BookingTitle(bookingId)}\nНапишите текст или <code>-</code>, чтобы пропустить.",
                parseMode: ParseMode.Html,
                replyMarkup: new ForceReplyMarkup { Selective = true },
                cancellationToken: cancellationToken);

            await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram callback handling failed");
            await botClient.AnswerCallbackQuery(
                callbackQuery.Id,
                "Произошла ошибка",
                showAlert: true,
                cancellationToken: cancellationToken);
        }
    }

    private async Task SendUnknownCommandResponse(
        ITelegramBotClient botClient,
        long chatId,
        CancellationToken cancellationToken)
    {
        const string caption = "🤔 <b>Неизвестная команда</b>\n\nДля вызова меню используйте /start";

        if (string.IsNullOrWhiteSpace(_unknownCommandVideo))
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: caption,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            return;
        }

        try
        {
            if (File.Exists(_unknownCommandVideo))
            {
                await using var video = File.OpenRead(_unknownCommandVideo);
                await botClient.SendVideo(
                    chatId: chatId,
                    video: InputFile.FromStream(video, System.IO.Path.GetFileName(_unknownCommandVideo)),
                    caption: caption,
                    parseMode: ParseMode.Html,
                    supportsStreaming: true,
                    cancellationToken: cancellationToken);
                return;
            }

            InputFile inputFile = Uri.TryCreate(_unknownCommandVideo, UriKind.Absolute, out var videoUri)
                ? InputFile.FromUri(videoUri)
                : InputFile.FromFileId(_unknownCommandVideo);

            await botClient.SendVideo(
                chatId: chatId,
                video: inputFile,
                caption: caption,
                parseMode: ParseMode.Html,
                supportsStreaming: true,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unknown command video sending failed");
            await botClient.SendMessage(
                chatId: chatId,
                text: caption,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleCommentReply(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        if (!_pendingComments.TryGetValue(chatId, out var pendingData))
            return;

        string successMessage;
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var bookingService = scope.ServiceProvider.GetRequiredService<BookingService>();
            var userService = scope.ServiceProvider.GetRequiredService<UserService>();

            var admin = await userService.GetByTelegramChatIdAsync(chatId);
            if (admin is null)
                return;

            var comment = message.Text == "-" ? null : message.Text;
            var adminComment = FormatAdminComment(admin, comment);

            if (pendingData.Action == "approve")
            {
                await bookingService.ApproveBookingAsync(pendingData.BookingId, adminComment);
                successMessage = $"✅ {TelegramText.BookingTitle(pendingData.BookingId)}\nОдобрено";
            }
            else
            {
                await bookingService.CancelBookingAsync(pendingData.BookingId, admin.Id, true, adminComment);
                successMessage = $"❌ {TelegramText.BookingTitle(pendingData.BookingId)}\nОтклонено";
            }

            _pendingComments.TryRemove(chatId, out _);
        }
        catch (GraphQLException ex)
        {
            _pendingComments.TryRemove(chatId, out _);
            _logger.LogWarning(ex, "Telegram booking comment rejected");
            await botClient.SendMessage(
                chatId,
                $"❌ {TelegramText.Escape(ex.Message)}",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram booking comment handling failed");
            await botClient.SendMessage(
                chatId,
                "❌ Не удалось обработать бронирование. Попробуйте отправить комментарий еще раз.",
                cancellationToken: cancellationToken);
            return;
        }

        try
        {
            await botClient.SendMessage(
                chatId,
                successMessage,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram booking confirmation message failed");
        }
    }

    private static string? FormatAdminComment(Models.User admin, string? comment)
    {
        return string.IsNullOrWhiteSpace(comment)
            ? null
            : $"{admin.Name}: {comment.Trim()}";
    }

    private async Task UpdateUsername(long chatId, string? username)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var userService = scope.ServiceProvider.GetRequiredService<UserService>();
            await userService.UpdateTelegramUsernameAsync(chatId, username);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram username update failed for ChatId {ChatId}", chatId);
        }
    }

    private async Task ExecuteCommand(Type commandType, ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            ICommand command = commandType == typeof(BookingFilterCommand)
                ? BookingFilterCommand.Create(scope.ServiceProvider, message.Text)
                : (ICommand)ActivatorUtilities.CreateInstance(scope.ServiceProvider, commandType);

            await command.ExecuteAsync(botClient, message, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram command {Command} failed", commandType.Name);
            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: "❌ Произошла ошибка при выполнении команды",
                cancellationToken: cancellationToken);
        }
    }
}
