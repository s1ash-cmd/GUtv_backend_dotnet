using System.Net;
using GUtv_backend_dotnet.Models;

namespace GUtv_backend_dotnet.Services.Telegram.Commands;

public static class TelegramText
{
    public static string Escape(string? value)
    {
        return WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(value) ? "-" : value.Trim());
    }

    public static string Code(string? value)
    {
        return $"<code>{Escape(value)}</code>";
    }

    public static string Period(DateTime start, DateTime end)
    {
        return $"{start:dd.MM.yyyy HH:mm} - {end:dd.MM.yyyy HH:mm}";
    }

    public static string BookingTitle(int bookingId)
    {
        return $"<b>Бронирование #{bookingId}</b>";
    }

    public static string GetRole(UserRole role)
    {
        return role switch
        {
            UserRole.Admin => "Администратор",
            UserRole.Ronin => "Пользователь",
            UserRole.Osnova => "Пользователь",
            UserRole.User => "Пользователь",
            _ => role.ToString()
        };
    }

    public static string HasRoninAccess(UserRole role)
    {
        return role is UserRole.Admin or UserRole.Ronin ? "Да" : "Нет";
    }

    public static string GetStatusEmoji(BookingStatus status)
    {
        return status switch
        {
            BookingStatus.Pending => "⏳",
            BookingStatus.Approved => "✅",
            BookingStatus.Completed => "🏁",
            BookingStatus.Cancelled => "❌",
            _ => ""
        };
    }

    public static string GetStatusName(BookingStatus status)
    {
        return status switch
        {
            BookingStatus.Pending => "Ожидает",
            BookingStatus.Approved => "Одобрено",
            BookingStatus.Completed => "Завершено",
            BookingStatus.Cancelled => "Отменено",
            _ => status.ToString()
        };
    }
}
