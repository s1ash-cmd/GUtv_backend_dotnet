using GUtv_backend_dotnet.Models;

namespace GUtv_backend_dotnet.Services;

public static class BookingAdminCommentFormatter
{
    // The Telegram prompt asks for "-" to skip the comment; phone keyboards may
    // turn it into a dash or add whitespace, so all of these mean "no comment".
    private static readonly string[] SkipMarkers = ["-", "–", "—"];

    public static bool IsBlank(string? comment) =>
        string.IsNullOrWhiteSpace(comment) || SkipMarkers.Contains(comment.Trim());

    public static string? Format(User admin, string? comment) =>
        IsBlank(comment) ? null : $"{admin.Name}: {comment!.Trim()}";
}
