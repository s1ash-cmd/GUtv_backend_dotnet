namespace GUtv_backend_dotnet.Models;

public class User
{
    public int Id { get; set; }
    public string Login { get; set; } = "";
    [GraphQLIgnore]
    public string PasswordHash { get; set; } = "";
    public string Name { get; set; } = "";

    public long? TelegramChatId { get; set; }
    public string? TelegramUsername { get; set; }
    [GraphQLIgnore]
    public string? TelegramLinkCode { get; set; }
    [GraphQLIgnore]
    public DateTime? TelegramLinkCodeExpiry { get; set; }

    public UserRole Role { get; set; } = UserRole.User;
    public bool Banned { get; set; }
    public int JoinYear { get; set; }
    [GraphQLIgnore]
    public string? RefreshToken { get; set; }
    [GraphQLIgnore]
    public DateTime? RefreshTokenExpiryTime { get; set; }

    public ICollection<Booking> Bookings { get; set; } = [];
}