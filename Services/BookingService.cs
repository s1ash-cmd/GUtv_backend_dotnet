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

        await using var transaction = await db.Database.BeginTransactionAsync();

        var warnings = new Dictionary<string, object>();
        if ((input.StartTime - DateTime.UtcNow).TotalDays < 3)
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
        foreach (var requestedItem in NormalizeRequestedEquipment(input.Equipment))
        {
            var eqModel = await db.EqModels
                .FirstOrDefaultAsync(m => m.Name == requestedItem.ModelName);

            if (eqModel == null)
                throw new GraphQLException($"Модель оборудования {requestedItem.ModelName} не найдена");

            await AcquireAdvisoryLockAsync(1, eqModel.Id);

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
                var conflicts = await GetBookingConflictsAsync(eqModel.Id, input.StartTime, input.EndTime);
                throw new GraphQLException(
                    FormatConflictMessage(
                        requestedItem.ModelName,
                        availableItems.Count,
                        requestedItem.Quantity,
                        conflicts));
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
        await transaction.CommitAsync();

        var createdBooking = await GetBookingEntityByIdAsync(booking.Id);
        await telegramNotificationService.NotifyAdminsNewBooking(createdBooking);

        return createdBooking;
    }

    public async Task<Booking> UpdateBookingAsync(
        int bookingId,
        CreateBookingInput input,
        int actorUserId,
        bool isAdmin)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await AcquireAdvisoryLockAsync(2, bookingId);

        var booking = await db.Bookings
            .Include(b => b.BookingItems)
            .FirstOrDefaultAsync(b => b.Id == bookingId)
            ?? throw new GraphQLException($"Бронирование с ID {bookingId} не найдено");
        var bookingUser = await db.Users.FindAsync(booking.UserId)
            ?? throw new GraphQLException("Владелец бронирования не найден");

        if (!isAdmin && booking.UserId != actorUserId)
            throw new GraphQLException("Вы не можете изменить чужое бронирование");

        if (!isAdmin && booking.Status is not (BookingStatus.Pending or BookingStatus.Approved))
            throw new GraphQLException("Изменить можно только ожидающее или одобренное бронирование");

        if (input.StartTime >= input.EndTime)
            throw new GraphQLException("Дата начала должна быть раньше даты окончания");

        if (string.IsNullOrWhiteSpace(input.Reason))
            throw new GraphQLException("Причина бронирования не может быть пустой");

        if (input.Equipment is null || input.Equipment.Count == 0)
            throw new GraphQLException("Не выбрано оборудование для бронирования");

        var replacementItems = new List<BookingItem>();
        foreach (var requestedItem in NormalizeRequestedEquipment(input.Equipment))
        {
            var eqModel = await db.EqModels
                .FirstOrDefaultAsync(m => m.Name == requestedItem.ModelName)
                ?? throw new GraphQLException($"Модель оборудования {requestedItem.ModelName} не найдена");

            await AcquireAdvisoryLockAsync(1, eqModel.Id);

            if (!isAdmin && !HasEquipmentAccess(bookingUser.Role, eqModel.Access))
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
                requestedItem.Quantity,
                bookingId);

            if (availableItems.Count < requestedItem.Quantity)
            {
                var conflicts = await GetBookingConflictsAsync(
                    eqModel.Id,
                    input.StartTime,
                    input.EndTime,
                    bookingId);
                throw new GraphQLException(
                    FormatConflictMessage(
                        requestedItem.ModelName,
                        availableItems.Count,
                        requestedItem.Quantity,
                        conflicts));
            }

            replacementItems.AddRange(availableItems.Select(eqItem => new BookingItem
            {
                BookingId = booking.Id,
                EqItemId = eqItem.Id,
                StartDate = input.StartTime,
                EndDate = input.EndTime,
                IsReturned = false
            }));
        }

        var warnings = new Dictionary<string, object>();
        if ((input.StartTime - DateTime.UtcNow).TotalDays < 3)
            warnings["invalidDate"] = "Бронирование создается меньше чем за 3 дня";

        db.BookingItems.RemoveRange(booking.BookingItems);
        booking.BookingItems = replacementItems;
        booking.Reason = input.Reason.Trim();
        booking.StartTime = input.StartTime;
        booking.EndTime = input.EndTime;
        booking.Comment = input.Comment;
        booking.AdminComment = null;
        booking.Status = BookingStatus.Pending;
        booking.WarningsJson = SerializeWarnings(warnings);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        var updatedBooking = await GetBookingEntityByIdAsync(booking.Id);
        await telegramNotificationService.NotifyAdminsBookingUpdated(updatedBooking);
        return updatedBooking;
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

        return bookings;
    }

    public async Task<List<Booking>> GetAllBookingsAsync()
    {
        return await FindBookingWithIncludes().ToListAsync();
    }

    public async Task<List<Booking>> GetCalendarBookingsAsync(DateTime? start = null, DateTime? end = null)
    {
        var query = FindBookingWithIncludes()
            .Where(b => b.Status == BookingStatus.Pending || b.Status == BookingStatus.Approved);

        if (start.HasValue && end.HasValue)
            query = query.Where(b => b.StartTime < end.Value && b.EndTime > start.Value);

        return await query
            .OrderBy(b => b.StartTime)
            .ToListAsync();
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
        await using var transaction = await db.Database.BeginTransactionAsync();
        await AcquireAdvisoryLockAsync(2, bookingId);

        var booking = await db.Bookings.FindAsync(bookingId)
            ?? throw new GraphQLException($"Бронирование с ID {bookingId} не найдено");

        if (booking.Status != BookingStatus.Pending)
            throw new GraphQLException("Бронирование недоступно для обработки");

        var oldStatus = booking.Status;
        booking.Status = BookingStatus.Approved;
        if (!string.IsNullOrWhiteSpace(adminComment))
            booking.AdminComment = adminComment;

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        var updatedBooking = await GetBookingEntityByIdAsync(bookingId);
        await telegramNotificationService.NotifyUserBookingStatusChanged(updatedBooking, oldStatus);

        return updatedBooking;
    }

    public async Task<Booking> CompleteBookingAsync(int bookingId)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await AcquireAdvisoryLockAsync(2, bookingId);

        var booking = await db.Bookings.FindAsync(bookingId)
            ?? throw new GraphQLException($"Бронирование с ID {bookingId} не найдено");

        if (booking.Status != BookingStatus.Approved)
            throw new GraphQLException("Завершить можно только одобренное бронирование");

        var oldStatus = booking.Status;
        booking.Status = BookingStatus.Completed;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        var updatedBooking = await GetBookingEntityByIdAsync(bookingId);
        await telegramNotificationService.NotifyUserBookingStatusChanged(updatedBooking, oldStatus);

        return updatedBooking;
    }

    public async Task<Booking> CancelBookingAsync(int bookingId, int userId, bool isAdmin, string? adminComment = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await AcquireAdvisoryLockAsync(2, bookingId);

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

        if (isAdmin && !string.IsNullOrWhiteSpace(adminComment))
            booking.AdminComment = adminComment;

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        var updatedBooking = await GetBookingEntityByIdAsync(bookingId);
        await telegramNotificationService.NotifyUserBookingStatusChanged(updatedBooking, oldStatus);

        return updatedBooking;
    }

    public string GetWarningsJson(Booking booking)
    {
        return string.IsNullOrWhiteSpace(booking.WarningsJson) ? "{}" : booking.WarningsJson;
    }

    private async Task<List<EqItem>> GetAvailableItemsAsync(
        int eqModelId,
        DateTime start,
        DateTime end,
        int requiredCount,
        int? excludedBookingId = null)
    {
        var items = await db.EqItems
            .Include(i => i.EqModel)
            .Where(i => i.EqModelId == eqModelId)
            .Where(i => i.Operable)
            .Where(i => !i.BookingItems.Any(bi =>
                (!excludedBookingId.HasValue || bi.BookingId != excludedBookingId.Value) &&
                (bi.Booking.Status == BookingStatus.Pending ||
                 bi.Booking.Status == BookingStatus.Approved) &&
                start < bi.EndDate && end > bi.StartDate))
            .OrderBy(i => i.InventoryNumber)
            .Take(requiredCount + 1)
            .ToListAsync();

        return items.Take(requiredCount).ToList();
    }

    private async Task<List<BookingItem>> GetBookingConflictsAsync(
        int eqModelId,
        DateTime start,
        DateTime end,
        int? excludedBookingId = null)
    {
        return await db.BookingItems
            .AsNoTracking()
            .Include(bi => bi.Booking)
            .ThenInclude(b => b.User)
            .Include(bi => bi.EqItem)
            .ThenInclude(i => i.EqModel)
            .Where(bi => bi.EqItem.EqModelId == eqModelId)
            .Where(bi => !excludedBookingId.HasValue || bi.BookingId != excludedBookingId.Value)
            .Where(bi =>
                (bi.Booking.Status == BookingStatus.Pending ||
                 bi.Booking.Status == BookingStatus.Approved) &&
                start < bi.EndDate && end > bi.StartDate)
            .OrderBy(bi => bi.StartDate)
            .ToListAsync();
    }

    private static string FormatConflictMessage(
        string modelName,
        int availableCount,
        int requiredCount,
        IReadOnlyList<BookingItem> conflicts)
    {
        var baseMessage =
            $"Недостаточно {modelName}: свободно {availableCount}, нужно {requiredCount}.";

        if (conflicts.Count == 0)
            return baseMessage;

        var groupedConflicts = conflicts
            .GroupBy(conflict => new
            {
                conflict.BookingId,
                conflict.StartDate,
                conflict.EndDate,
                conflict.Booking.User.Name,
                conflict.Booking.User.Login,
                conflict.Booking.User.TelegramUsername
            })
            .OrderBy(group => group.Key.StartDate)
            .ThenBy(group => group.Key.Name)
            .ToList();

        var details = groupedConflicts
            .Take(4)
            .Select(group =>
            {
                var contact = string.IsNullOrWhiteSpace(group.Key.TelegramUsername)
                    ? $"@{group.Key.Login}"
                    : $"@{group.Key.TelegramUsername}";
                var inventoryNumbers = group
                    .Select(conflict => conflict.EqItem.InventoryNumber)
                    .Distinct()
                    .Order()
                    .ToList();
                var countText = inventoryNumbers.Count == 1
                    ? "1 экземпляр"
                    : $"{inventoryNumbers.Count} шт.";

                return $"{group.Key.Name} ({contact}) — {group.Key.StartDate:dd.MM HH:mm}–{group.Key.EndDate:dd.MM HH:mm}: {countText}, {FormatInventoryNumbers(inventoryNumbers)}";
            });

        var rest = groupedConflicts.Count > 4
            ? $" Еще бронирований: {groupedConflicts.Count - 4}."
            : string.Empty;

        return $"{baseMessage} Уже занято: {string.Join("; ", details)}.{rest}";

        static string FormatInventoryNumbers(IReadOnlyList<string> inventoryNumbers)
        {
            const int visibleCount = 4;
            var visible = inventoryNumbers.Take(visibleCount).ToList();
            var rest = inventoryNumbers.Count - visible.Count;
            var suffix = rest > 0 ? $" и еще {rest}" : string.Empty;

            return $"инв. {string.Join(", ", visible)}{suffix}";
        }
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

    private static IReadOnlyList<CreateBookingEquipmentInput> NormalizeRequestedEquipment(
        IReadOnlyList<CreateBookingEquipmentInput> equipment)
    {
        foreach (var item in equipment)
        {
            if (string.IsNullOrWhiteSpace(item.ModelName))
                throw new GraphQLException("Название модели оборудования не может быть пустым");

            if (item.Quantity <= 0)
                throw new GraphQLException($"Количество для модели {item.ModelName} должно быть больше 0");
        }

        return equipment
            .GroupBy(item => item.ModelName.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CreateBookingEquipmentInput(
                group.Key,
                group.Sum(item => item.Quantity)))
            .ToList();
    }

    private async Task AcquireAdvisoryLockAsync(int lockGroup, int entityId)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockGroup}, {entityId})");
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
