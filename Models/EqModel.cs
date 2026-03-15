using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GUtv_backend_dotnet.Models;

public class EqModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public EqCategory Category { get; set; }
    public EqAccess Access { get; set; } = EqAccess.User;

    public string AttributesJson { get; set; } = "{}";

    public List<EqItem> EqItems { get; set; } = [];
}