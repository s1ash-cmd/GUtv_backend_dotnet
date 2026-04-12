namespace GUtv_backend_dotnet.Models;

public class Cart
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Reason { get; set; } = "";
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? Comment { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<CartItem> Items { get; set; } = [];
}
