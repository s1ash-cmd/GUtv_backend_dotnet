namespace GUtv_backend_dotnet.Models;

public class EqItem
{
    public int Id { get; set; }
    public int EqModelId { get; set; }
    public EqModel EqModel { get; set; } = null!;
    public string InventoryNumber { get; set; } = string.Empty;
    public bool Operable { get; set; } = true;
    // Set when a unit with booking history is written off: it disappears from the catalogue
    // and from selection, while past bookings keep pointing at it.
    [GraphQLIgnore]
    public DateTime? ArchivedAt { get; set; }
    // Not exposed: equipment is public, its booking history (and through it users) is not.
    [GraphQLIgnore]
    public List<BookingItem> BookingItems { get; set; } = [];
}