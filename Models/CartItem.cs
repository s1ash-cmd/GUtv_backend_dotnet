namespace GUtv_backend_dotnet.Models;

public class CartItem
{
    public int Id { get; set; }
    public int CartId { get; set; }
    public Cart Cart { get; set; } = null!;
    public int EqModelId { get; set; }
    public EqModel EqModel { get; set; } = null!;
    public int Quantity { get; set; }
}
