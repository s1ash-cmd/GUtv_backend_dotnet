namespace GUtv_backend_dotnet.Models;

public record CalendarBookingPayload(
    int Id,
    string UserName,
    string? TelegramUsername,
    string Reason,
    DateTime StartTime,
    DateTime EndTime,
    BookingStatus Status,
    IReadOnlyList<CalendarEquipmentPayload> Equipment);

public record CalendarEquipmentPayload(int Id, string ModelName, string InventoryNumber);
