using GUtv_backend_dotnet.Models;

namespace GUtv_backend_dotnet.Services;

public static class BookingAdminCommentFormatter
{
    public static string? Format(User admin, string? comment) =>
        string.IsNullOrWhiteSpace(comment) ? null : $"{admin.Name}: {comment.Trim()}";
}
