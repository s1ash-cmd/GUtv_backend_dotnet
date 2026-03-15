namespace GUtv_backend_dotnet.Models;

public class Event
{
    public int Id { get; set; }

    public string Client { get; set; } = null!;

    public string Reason { get; set; } = "";
    public DateTime CreationTime { get; set; } = DateTime.UtcNow;
    public BookingStatus Status { get; set; } = BookingStatus.Pending;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }

    public string WarningsJson { get; set; } = "{}";

    public string? Comment { get; set; }
    public string? AdminComment { get; set; }
}