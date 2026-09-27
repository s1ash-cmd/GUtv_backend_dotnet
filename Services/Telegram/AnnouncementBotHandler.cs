using System.Collections.Concurrent;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services.Telegram.Commands;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace GUtv_backend_dotnet.Services.Telegram;

public class AnnouncementBotHandler(IServiceScopeFactory scopeFactory, ILogger<AnnouncementBotHandler> logger)
{
    private sealed record Draft(Guid RequestId, int AuthorId, int PromptId, DateTime ExpiresAt,
        string? Title = null, string? Body = null);
    private readonly ConcurrentDictionary<long, Draft> _drafts = new();

    public void CancelDraft(long chatId) => _drafts.TryRemove(chatId, out _);

    public async Task<bool> HandleMessageAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var text = message.Text;
        var command = text?.Split([' ', '\n'], 2)[0].Split('@')[0];
        var start = command == "/announce" || text == "📢 Написать объявление";
        // Expired previews must not accumulate indefinitely in a long-running bot.
        foreach (var entry in _drafts.Where(d => d.Value.ExpiresAt <= DateTime.UtcNow))
            _drafts.TryRemove(entry);

        _drafts.TryGetValue(chatId, out var draft);
        if (!start && draft is null) return false;
        if (!start && text?.StartsWith('/') == true)
        {
            _drafts.TryRemove(chatId, out _);
            if (command != "/cancel") return false;
            await bot.SendMessage(chatId, "Публикация отменена.", cancellationToken: ct);
            return true;
        }

        using var scope = scopeFactory.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserService>().GetByTelegramChatIdAsync(chatId);
        if (user is null || user.Banned || user.Role != UserRole.Admin)
        {
            _drafts.TryRemove(chatId, out _);
            await bot.SendMessage(chatId, "Публиковать объявления могут только администраторы с привязанным аккаунтом.", cancellationToken: ct);
            return true;
        }

        if (start)
        {
            var prompt = await bot.SendMessage(chatId,
                "📢 Ответьте на это сообщение: первая строка — заголовок (до 100 символов), остальные — текст (до 3000).\n\nПеред рассылкой появится предпросмотр. Отмена: /cancel",
                replyMarkup: new ForceReplyMarkup { Selective = true }, cancellationToken: ct);
            _drafts[chatId] = new Draft(Guid.NewGuid(), user.Id, prompt.MessageId, DateTime.UtcNow.AddMinutes(30));
            return true;
        }

        if (draft!.Title is not null)
        {
            await bot.SendMessage(chatId, "Подтвердите объявление кнопкой под предпросмотром. Для нового текста — /announce, для отмены — /cancel.", cancellationToken: ct);
            return true;
        }
        if (message.ReplyToMessage?.MessageId != draft.PromptId || text is null)
        {
            await bot.SendMessage(chatId, "Отправьте заголовок и текст ответом на запрос объявления. Отмена: /cancel.", cancellationToken: ct);
            return true;
        }

        var lines = text.Split('\n', 2);
        var title = lines[0].Trim();
        var body = lines.Length == 2 ? lines[1].Trim() : "";
        try { AnnouncementService.Validate(title, body); }
        catch (GraphQLException ex)
        {
            await bot.SendMessage(chatId, ex.Message, cancellationToken: ct);
            return true;
        }
        var ready = draft with { Title = title, Body = body };
        _drafts[chatId] = ready;
        var preview = TelegramText.AnnouncementMessage(user.Name, title, body,
            prefix: "Предпросмотр\n\n",
            suffix: "\n\nПосле публикации объявление появится на сайте и будет отправлено всем участникам с привязанным Telegram.");
        await bot.SendMessage(chatId,
            preview.Text,
            entities: preview.Entities,
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("Опубликовать и разослать", $"announce:publish:{ready.RequestId:N}"),
                    InlineKeyboardButton.WithCallbackData("Отмена", $"announce:cancel:{ready.RequestId:N}")
                }
            }), cancellationToken: ct);
        return true;
    }

    public async Task HandleCallbackAsync(ITelegramBotClient bot, CallbackQuery callback, CancellationToken ct)
    {
        var chatId = callback.Message!.Chat.Id;
        var parts = callback.Data!.Split(':');
        if (parts.Length != 3 || !Guid.TryParse(parts[2], out var requestId) ||
            !_drafts.TryGetValue(chatId, out var draft) || draft.RequestId != requestId ||
            draft.ExpiresAt <= DateTime.UtcNow || draft.Title is null || draft.Body is null)
        {
            await bot.AnswerCallbackQuery(callback.Id, "Предпросмотр устарел или уже обработан. Создайте объявление через /announce.", showAlert: true, cancellationToken: ct);
            return;
        }
        if (parts[1] == "cancel")
        {
            _drafts.TryRemove(new KeyValuePair<long, Draft>(chatId, draft));
            await bot.AnswerCallbackQuery(callback.Id, "Публикация отменена", cancellationToken: ct);
            return;
        }
        if (parts[1] != "publish") return;

        await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
        Announcement published;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var user = await scope.ServiceProvider.GetRequiredService<UserService>().GetByTelegramChatIdAsync(chatId);
            if (user is null || user.Id != draft.AuthorId || user.Banned || user.Role != UserRole.Admin)
                throw new GraphQLException("Недостаточно прав для публикации объявления");
            published = await scope.ServiceProvider.GetRequiredService<AnnouncementService>()
                .PublishAsync(user.Id, draft.RequestId, draft.Title, draft.Body, ct);
            _drafts.TryRemove(new KeyValuePair<long, Draft>(chatId, draft));
        }
        catch (GraphQLException ex)
        {
            await bot.SendMessage(chatId, ex.Message, cancellationToken: ct);
            return;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Publishing announcement from Telegram failed");
            await bot.SendMessage(chatId, "Не удалось подтвердить публикацию. Нажмите ту же кнопку ещё раз: повторного объявления не будет.", cancellationToken: ct);
            return;
        }
        await bot.SendMessage(chatId, $"✅ Объявление #{published.Id} опубликовано на сайте. Рассылка в Telegram поставлена в очередь.", cancellationToken: ct);
    }
}
