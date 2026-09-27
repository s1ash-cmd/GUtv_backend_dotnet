using Telegram.Bot.Types.ReplyMarkups;

namespace GUtv_backend_dotnet.Services.Telegram.Commands;

public static class TelegramKeyboards
{
    public static ReplyKeyboardMarkup ForUser(Models.User user) =>
        user.Role == Models.UserRole.Admin && !user.Banned
            ? new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "👤 Профиль", "📆 Мои бронирования" },
                new KeyboardButton[] { "📢 Написать объявление" },
                new KeyboardButton[] { "ℹ️ Помощь" }
            }) { ResizeKeyboard = true, IsPersistent = true }
            : MainMenu;

    public static ReplyKeyboardMarkup MainMenu => new(new[]
    {
        new KeyboardButton[] { "👤 Профиль", "📆 Мои бронирования" },
        new KeyboardButton[] { "ℹ️ Помощь" }
    })
    {
        ResizeKeyboard = true,
        IsPersistent = true
    };
}
