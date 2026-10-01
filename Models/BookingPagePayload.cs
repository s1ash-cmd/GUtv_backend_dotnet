namespace GUtv_backend_dotnet.Models;

public record BookingPagePayload(
    IReadOnlyList<Booking> Items,
    int TotalCount,
    int Page,
    int PageSize);
