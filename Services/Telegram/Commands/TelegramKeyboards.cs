using Telegram.Bot.Types.ReplyMarkups;

namespace GUtv_backend_dotnet.Services.Telegram.Commands;

public static class TelegramKeyboards
{
    public static ReplyKeyboardMarkup MainMenu => new(new[]
    {
        new KeyboardButton[] { "👤 Профиль", "📆 Мои бронирования" },
        new KeyboardButton[] { "ℹ️ Помощь" }
    })
    {
        ResizeKeyboard = true
    };
}
