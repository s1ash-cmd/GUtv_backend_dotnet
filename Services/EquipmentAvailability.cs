using GUtv_backend_dotnet.Models;

namespace GUtv_backend_dotnet.Services;

internal static class EquipmentAvailability
{
    // Half-open intervals allow a new booking to start exactly when an earlier one ends.
    public static IQueryable<EqItem> ForModel(
        IQueryable<EqItem> items,
        int modelId,
        DateTime start,
        DateTime end,
        int? excludedBookingId = null) =>
        items.Where(item => item.EqModelId == modelId && item.Operable)
            .Where(item => !item.BookingItems.Any(bookingItem =>
                (!excludedBookingId.HasValue || bookingItem.BookingId != excludedBookingId.Value) &&
                (bookingItem.Booking.Status == BookingStatus.Pending ||
                 bookingItem.Booking.Status == BookingStatus.Approved) &&
                start < bookingItem.EndDate && end > bookingItem.StartDate));
}
