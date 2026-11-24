namespace GUtv_backend_dotnet.Models;

public class BookingEquipmentChange
{
    public int BookingId { get; set; }
    public int Revision { get; set; }
    public int ActorUserId { get; set; }
    public string ActorName { get; set; } = "";
    public DateTime ChangedAt { get; set; }
    public string EquipmentJson { get; set; } = "";
}

public sealed record BookingEquipmentSnapshot(int EqItemId, string ModelName, string InventoryNumber);

public sealed record BookingEquipmentChangePayload(
    int Revision,
    string ActorName,
    DateTime ChangedAt,
    IReadOnlyList<BookingEquipmentSnapshot> Added,
    IReadOnlyList<BookingEquipmentSnapshot> Removed);

public enum BookingHistoryField
{
    StartTime,
    EndTime,
    Reason,
    Comment,
    AdminComment,
    Status
}

public sealed record BookingFieldChange(BookingHistoryField Field, string? Before, string? After);

public sealed record BookingChangePayload(
    int Revision,
    string ActorName,
    DateTime ChangedAt,
    IReadOnlyList<BookingEquipmentSnapshot> Added,
    IReadOnlyList<BookingEquipmentSnapshot> Removed,
    IReadOnlyList<BookingFieldChange> Fields);
