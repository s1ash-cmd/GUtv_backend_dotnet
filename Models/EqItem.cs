namespace GUtv_backend_dotnet.Models;

public class EqItem
{
    public int Id { get; set; }
    public int EqModelId { get; set; }
    public EqModel EqModel { get; set; } = null!;
    public string InventoryNumber { get; set; } = string.Empty;
    public bool Operable { get; set; } = true;
    // Not exposed: equipment is public, its booking history (and through it users) is not.
    [GraphQLIgnore]
    public List<BookingItem> BookingItems { get; set; } = [];
}