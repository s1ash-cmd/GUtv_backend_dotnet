namespace GUtv_backend_dotnet.Models;

public class UserSession
{
    public Guid Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    [GraphQLIgnore]
    public string RefreshTokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? UserAgent { get; set; }
}
