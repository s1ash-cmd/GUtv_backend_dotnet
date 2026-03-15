namespace GUtv_backend_dotnet.Models;

public class BookingItem
{
    public int Id { get; set; }

    public int BookingId { get; set; }
    public Booking Booking { get; set; } = null!;

    public int EqItemId { get; set; }
    public EqItem EqItem { get; set; } = null!;

    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsReturned { get; set; } = false;
}