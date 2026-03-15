namespace GUtv_backend_dotnet.Models;

public class EqPhoto
{
    public int Id { get; set; }
    public int EqModelId { get; set; }
    public EqModel EqModel { get; set; } = null!;
    public string Url { get; set; } = string.Empty;
    public int Order { get; set; } = 0;
}