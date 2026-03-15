namespace GUtv_backend_dotnet.Models;

public class User
{
    public int Id { get; set; }
    public string Login { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Name { get; set; } = "";

    public long? TelegramChatId { get; set; }
    public string? TelegramUsername { get; set; }
    public string? TelegramLinkCode { get; set; }
    public DateTime? TelegramLinkCodeExpiry { get; set; }

    public UserRole Role { get; set; } = UserRole.User;
    public bool Banned { get; set; }
    public int JoinYear { get; set; }

    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiryTime { get; set; }

    public ICollection<Booking> Bookings { get; set; } = [];
}