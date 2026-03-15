namespace GUtv_backend_dotnet.Models;

public class EqItem
{
    public int Id { get; set; }
    public int EqModelId { get; set; }
    public EqModel EqModel { get; set; } = null!;
    public string InventoryNumber { get; set; } = string.Empty;
    public bool Operable { get; set; } = true;
    public List<BookingItem> BookingItems { get; set; } = [];
}