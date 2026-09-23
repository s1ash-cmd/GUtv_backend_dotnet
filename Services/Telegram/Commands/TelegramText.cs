using System.Net;
using GUtv_backend_dotnet.Models;

namespace GUtv_backend_dotnet.Services.Telegram.Commands;

public static class TelegramText
{
    private static readonly TimeZoneInfo BookingTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

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
        return $"{ToBookingTime(start):dd.MM.yyyy HH:mm} - {ToBookingTime(end):dd.MM.yyyy HH:mm} (МСК)";
    }

    private static DateTime ToBookingTime(DateTime value)
    {
        // Booking timestamps are stored in UTC. Do not use the server's local timezone.
        var utc = value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, BookingTimeZone);
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
