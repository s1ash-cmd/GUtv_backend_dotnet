using System.Text.Json;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services.Telegram;
using HotChocolate;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.Services;

public class BookingService(AppDbContext db, TelegramNotificationService telegramNotificationService)
{
    public async Task<Booking> CreateBookingAsync(CreateBookingInput input, int userId)
    {
        var user = await db.Users.FindAsync(userId)
            ?? throw new GraphQLException("Пользователь не найден");

        if (input.StartTime >= input.EndTime)
            throw new GraphQLException("Дата начала должна быть раньше даты окончания");

        if (input.Equipment is null || input.Equipment.Count == 0)
            throw new GraphQLException("Не выбрано оборудование для бронирования");

        var warnings = new Dictionary<string, object>();
        if ((input.StartTime - DateTime.UtcNow).TotalDays < 2)
            warnings["invalidDate"] = "Бронирование создается меньше чем за 3 дня";

        var booking = new Booking
        {
            UserId = user.Id,
            Reason = input.Reason.Trim(),
            CreationTime = DateTime.UtcNow,
            StartTime = input.StartTime,
            EndTime = input.EndTime,
            Status = BookingStatus.Pending,
            Comment = input.Comment,
            WarningsJson = SerializeWarnings(warnings)
        };

        var bookingItems = new List<BookingItem>();
        foreach (var requestedItem in input.Equipment)
        {
            if (requestedItem.Quantity <= 0)
                throw new GraphQLException($"Количество для модели {requestedItem.ModelName} должно быть больше 0");

            var eqModel = await db.EqModels
                .FirstOrDefaultAsync(m => m.Name == requestedItem.ModelName);

            if (eqModel == null)
                throw new GraphQLException($"Модель оборудования {requestedItem.ModelName} не найдена");

            if (!HasEquipmentAccess(user.Role, eqModel.Access))
            {
                throw eqModel.Access switch
                {
                    EqAccess.Ronin => new GraphQLException(
                        $"У вас нет доступа к оборудованию {requestedItem.ModelName}. Требуется разрешение на Ronin"),
                    EqAccess.Osnova => new GraphQLException(
                        $"У вас нет доступа к оборудованию {requestedItem.ModelName}. Требуется быть в основе"),
                    _ => new GraphQLException(
                        $"У вас нет доступа к оборудованию {requestedItem.ModelName}")
                };
            }

            var availableItems = await GetAvailableItemsAsync(
                eqModel.Id,
                input.StartTime,
                input.EndTime,
                requestedItem.Quantity);

            if (availableItems.Count < requestedItem.Quantity)
            {
                throw new GraphQLException(
                    $"Недостаточно доступного оборудования модели {requestedItem.ModelName}. " +
                    $"Доступно: {availableItems.Count}, требуется: {requestedItem.Quantity}");
            }

            bookingItems.AddRange(availableItems.Select(eqItem => new BookingItem
            {
                EqItemId = eqItem.Id,
                StartDate = input.StartTime,
                EndDate = input.EndTime,
                IsReturned = false
            }));
        }

        booking.BookingItems = bookingItems;

        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        var createdBooking = await GetBookingEntityByIdAsync(booking.Id);
        await telegramNotificationService.NotifyAdminsNewBooking(createdBooking);

        return createdBooking;
    }

    public async Task<Booking> GetBookingByIdAsync(int id, int currentUserId, bool isAdmin)
    {
        var booking = await FindBookingWithIncludes().FirstOrDefaultAsync(b => b.Id == id)
            ?? throw new GraphQLException($"Бронирование с ID {id} не найдено");

        if (!isAdmin && booking.UserId != currentUserId)
            throw new GraphQLException("Вы не можете просматривать чужое бронирование");

        if (booking.BookingItems.Count == 0)
            throw new GraphQLException("У бронирования нет связанных элементов оборудования");

        return booking;
    }

    public async Task<List<Booking>> GetBookingsByUserAsync(int userId)
    {
        var bookings = await FindBookingWithIncludes()
            .Where(b => b.UserId == userId)
            .ToListAsync();

        if (bookings.Count == 0)
            throw new GraphQLException($"У пользователя с ID {userId} нет бронирований");

        return bookings;
    }

    public async Task<List<Booking>> GetAllBookingsAsync()
    {
        return await FindBookingWithIncludes().ToListAsync();
    }

    public async Task<List<Booking>> GetBookingsByEquipmentItemAsync(int eqItemId)
    {
        var bookings = await FindBookingWithIncludes()
            .Where(b => b.BookingItems.Any(bi => bi.EqItemId == eqItemId))
            .ToListAsync();

        if (bookings.Count == 0)
            throw new GraphQLException($"Не найдено бронирований для оборудования с ID {eqItemId}");

        return bookings;
    }

    public async Task<List<Booking>> GetBookingsByStatusAsync(BookingStatus status)
    {
        var bookings = await FindBookingWithIncludes()
            .Where(b => b.Status == status)
            .ToListAsync();

        if (bookings.Count == 0)
            throw new GraphQLException($"Нет бронирований со статусом {status}");

        return bookings;
    }

    public async Task<List<Booking>> GetBookingsByInventoryNumberAsync(string inventoryNumber)
    {
        if (string.IsNullOrWhiteSpace(inventoryNumber))
            throw new GraphQLException("Инвентарный номер не может быть пустым");

        var eqItem = await db.EqItems
            .FirstOrDefaultAsync(i => i.InventoryNumber.ToLower() == inventoryNumber.ToLower())
            ?? throw new GraphQLException(
                $"Оборудование с инвентарным номером {inventoryNumber} не найдено");

        var bookings = await FindBookingWithIncludes()
            .Where(b => b.BookingItems.Any(bi => bi.EqItemId == eqItem.Id))
            .ToListAsync();

        if (bookings.Count == 0)
            throw new GraphQLException(
                $"Нет бронирований для оборудования с инвентарным номером {inventoryNumber}");

        return bookings;
    }

    public async Task<Booking> ApproveBookingAsync(int bookingId, string? adminComment = null)
    {
        var booking = await db.Bookings.FindAsync(bookingId)
            ?? throw new GraphQLException($"Бронирование с ID {bookingId} не найдено");

        if (booking.Status != BookingStatus.Pending)
            throw new GraphQLException("Бронирование недоступно для обработки");

        var oldStatus = booking.Status;
        booking.Status = BookingStatus.Approved;
        if (!string.IsNullOrWhiteSpace(adminComment))
            booking.AdminComment = adminComment;

        await db.SaveChangesAsync();
        var updatedBooking = await GetBookingEntityByIdAsync(bookingId);
        await telegramNotificationService.NotifyUserBookingStatusChanged(updatedBooking, oldStatus);

        return updatedBooking;
    }

    public async Task<Booking> CompleteBookingAsync(int bookingId)
    {
        var booking = await db.Bookings.FindAsync(bookingId)
            ?? throw new GraphQLException($"Бронирование с ID {bookingId} не найдено");

        if (booking.Status != BookingStatus.Approved)
            throw new GraphQLException("Завершить можно только одобренное бронирование");

        var oldStatus = booking.Status;
        booking.Status = BookingStatus.Completed;
        await db.SaveChangesAsync();
        var updatedBooking = await GetBookingEntityByIdAsync(bookingId);
        await telegramNotificationService.NotifyUserBookingStatusChanged(updatedBooking, oldStatus);

        return updatedBooking;
    }

    public async Task<Booking> CancelBookingAsync(int bookingId, int userId, bool isAdmin, string? adminComment = null)
    {
        var booking = await db.Bookings
            .Include(b => b.User)
            .FirstOrDefaultAsync(b => b.Id == bookingId)
            ?? throw new GraphQLException($"Бронирование с ID {bookingId} не найдено");

        var isOwner = booking.UserId == userId;

        if (!isAdmin && !isOwner)
            throw new GraphQLException("Вы не можете отменить чужое бронирование");

        if (booking.Status == BookingStatus.Cancelled)
            throw new GraphQLException("Это бронирование уже отменено");

        var canCancelAsOwner =
            isOwner &&
            (booking.Status == BookingStatus.Pending || booking.Status == BookingStatus.Approved);
        var canCancelAsAdmin = isAdmin && booking.Status == BookingStatus.Pending;

        if (!canCancelAsOwner && !canCancelAsAdmin)
            throw new GraphQLException("Бронирование недоступно для обработки");

        var oldStatus = booking.Status;
        booking.Status = BookingStatus.Cancelled;

        if (isAdmin && !isOwner && !string.IsNullOrWhiteSpace(adminComment))
            booking.AdminComment = adminComment;

        await db.SaveChangesAsync();
        var updatedBooking = await GetBookingEntityByIdAsync(bookingId);
        await telegramNotificationService.NotifyUserBookingStatusChanged(updatedBooking, oldStatus);

        return updatedBooking;
    }

    public string GetWarningsJson(Booking booking)
    {
        return string.IsNullOrWhiteSpace(booking.WarningsJson) ? "{}" : booking.WarningsJson;
    }

    private async Task<List<EqItem>> GetAvailableItemsAsync(int eqModelId, DateTime start, DateTime end, int requiredCount)
    {
        var items = await db.EqItems
            .Include(i => i.EqModel)
            .Where(i => i.EqModelId == eqModelId)
            .Where(i => i.Operable)
            .Where(i => !i.BookingItems.Any(bi =>
                (bi.Booking.Status == BookingStatus.Pending ||
                 bi.Booking.Status == BookingStatus.Approved) &&
                start < bi.EndDate && end > bi.StartDate))
            .Take(requiredCount + 1)
            .ToListAsync();

        return items.Take(requiredCount).ToList();
    }

    private IQueryable<Booking> FindBookingWithIncludes()
    {
        return db.Bookings
            .AsNoTracking()
            .Include(b => b.User)
            .Include(b => b.BookingItems)
            .ThenInclude(bi => bi.EqItem)
            .ThenInclude(i => i.EqModel);
    }

    private async Task<Booking> GetBookingEntityByIdAsync(int id)
    {
        return await FindBookingWithIncludes().FirstAsync(b => b.Id == id);
    }

    private static string SerializeWarnings(Dictionary<string, object> warnings)
    {
        return JsonSerializer.Serialize(warnings);
    }

    private static bool HasEquipmentAccess(UserRole role, EqAccess access)
    {
        return access switch
        {
            EqAccess.User => role is UserRole.User or UserRole.Osnova or UserRole.Ronin or UserRole.Admin,
            EqAccess.Osnova => role is UserRole.Osnova or UserRole.Ronin or UserRole.Admin,
            EqAccess.Ronin => role is UserRole.Ronin or UserRole.Admin,
            _ => false
        };
    }
}

public record CreateBookingInput(
    string Reason,
    DateTime StartTime,
    DateTime EndTime,
    string? Comment,
    IReadOnlyList<CreateBookingEquipmentInput> Equipment
);

public record CreateBookingEquipmentInput(
    string ModelName,
    int Quantity
);
