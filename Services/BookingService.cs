using System.Text.Json;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services.Telegram;
using HotChocolate;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.Services;

public class BookingService(
    AppDbContext db,
    TelegramNotificationService telegramNotificationService,
    BookingNotificationQueue? notificationQueue = null)
{
    // Unpaginated list queries predate the paged ones and are kept for old clients only.
    public const int LegacyListLimit = 200;
    public const int CalendarMaxDays = 100;
    private const int DefaultPageSize = 30;
    private const int EquipmentHistoryPageSize = 10;

    public async Task<Booking> CreateBookingAsync(CreateBookingInput input, int userId)
    {
        Booking booking;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            try
            {
                booking = await CreateBookingInTransactionAsync(input, userId);
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                db.ChangeTracker.Clear();
                throw;
            }
        }

        await NotifyBookingCreatedAsync(booking);
        return booking;
    }

    // The caller owns the transaction, including any cart changes, and sends notifications after commit.
    internal async Task<Booking> CreateBookingInTransactionAsync(CreateBookingInput input, int userId)
    {
        RequireTransaction();
        var user = await EnsureCanBookAsync(userId);

        ValidateBookingInput(input);

        var allocation = await AllocateEquipmentAsync(input, user);

        var booking = new Booking
        {
            UserId = user.Id,
            Reason = input.Reason.Trim(),
            CreationTime = DateTime.UtcNow,
            StartTime = input.StartTime,
            EndTime = input.EndTime,
            Status = BookingStatus.Pending,
            Comment = input.Comment,
            BookingItems = allocation.Items,
            WarningsJson = SerializeWarnings(allocation.Warnings)
        };

        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return await GetBookingEntityByIdAsync(booking.Id);
    }

    public async Task<Booking> UpdateBookingAsync(
        int bookingId,
        CreateBookingInput input,
        int actorUserId,
        bool isAdmin)
    {
        Booking booking;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            try
            {
                booking = await UpdateBookingInTransactionAsync(bookingId, input, actorUserId, isAdmin);
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                db.ChangeTracker.Clear();
                throw;
            }
        }

        await NotifyBookingUpdatedAsync(booking, actorUserId);
        return booking;
    }

    internal async Task<Booking> UpdateBookingInTransactionAsync(
        int bookingId,
        CreateBookingInput input,
        int actorUserId,
        bool isAdmin)
    {
        RequireTransaction();
        var actor = await EnsureCanBookAsync(actorUserId);
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

        ValidateBookingInput(input);

        var allocation = await AllocateEquipmentAsync(input, bookingUser, isAdmin, bookingId);
        await RecordEquipmentChangeAsync(booking, allocation.Items, actor);

        db.BookingItems.RemoveRange(booking.BookingItems);
        booking.BookingItems = allocation.Items;
        booking.Reason = input.Reason.Trim();
        booking.StartTime = input.StartTime;
        booking.EndTime = input.EndTime;
        booking.Comment = input.Comment;
        booking.AdminComment = null;
        booking.Revision++;
        booking.Status = BookingStatus.Pending;
        booking.WarningsJson = SerializeWarnings(allocation.Warnings);

        await db.SaveChangesAsync();
        return await GetBookingEntityByIdAsync(booking.Id);
    }

    private async Task RecordEquipmentChangeAsync(Booking booking, List<BookingItem> newItems, User actor)
    {
        var previousIds = booking.BookingItems.Select(item => item.EqItemId).ToHashSet();
        var currentIds = newItems.Select(item => item.EqItemId).ToHashSet();
        var changedIds = previousIds.ToHashSet();
        changedIds.SymmetricExceptWith(currentIds);
        if (changedIds.Count == 0)
            return;

        var snapshots = await db.EqItems.AsNoTracking()
            .Where(item => changedIds.Contains(item.Id))
            .OrderBy(item => item.EqModel.Name).ThenBy(item => item.InventoryNumber)
            .Select(item => new BookingEquipmentSnapshot(item.Id, item.EqModel.Name, item.InventoryNumber))
            .ToListAsync();

        db.BookingEquipmentChanges.Add(new BookingEquipmentChange
        {
            BookingId = booking.Id,
            Revision = booking.Revision + 1,
            ActorUserId = actor.Id,
            ActorName = string.IsNullOrWhiteSpace(actor.Name) ? actor.Login : actor.Name,
            ChangedAt = DateTime.UtcNow,
            EquipmentJson = JsonSerializer.Serialize(new EquipmentChangeDetails(
                snapshots.Where(item => currentIds.Contains(item.EqItemId)).ToArray(),
                snapshots.Where(item => previousIds.Contains(item.EqItemId)).ToArray()))
        });
    }

    private sealed record EquipmentChangeDetails(
        BookingEquipmentSnapshot[] Added, BookingEquipmentSnapshot[] Removed);

    internal Task NotifyBookingCreatedAsync(Booking booking) =>
        DispatchNotificationAsync(async (notifications, cancellationToken) =>
        {
            await notifications.NotifyAdminsNewBooking(booking, cancellationToken);
            await notifications.NotifyUserBookingCreated(booking, cancellationToken);
        });

    internal Task NotifyBookingUpdatedAsync(Booking booking, int actorUserId) =>
        DispatchNotificationAsync(async (notifications, cancellationToken) =>
        {
            await notifications.NotifyAdminsBookingUpdated(booking, cancellationToken);
            await notifications.NotifyUserBookingUpdated(booking, actorUserId, cancellationToken);
        });

    private Task NotifyBookingStatusChangedAsync(Booking booking, BookingStatus oldStatus, int actorUserId) =>
        DispatchNotificationAsync(async (notifications, cancellationToken) =>
        {
            await notifications.NotifyUserBookingStatusChanged(booking, oldStatus, cancellationToken);
            await notifications.NotifyAdminsBookingDecided(booking, oldStatus, actorUserId, cancellationToken);
        });

    // Telegram is slow or unreachable at times, so a request never waits for it: the queue
    // sends after the response. Without a queue (tests, tools) the messages are sent inline.
    private Task DispatchNotificationAsync(Func<TelegramNotificationService, CancellationToken, Task> send)
    {
        if (notificationQueue is null)
            return send(telegramNotificationService, CancellationToken.None);

        notificationQueue.Enqueue(send);
        return Task.CompletedTask;
    }

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Booking changes require a transaction owned by the caller.");
    }

    public async Task<Booking> GetBookingByIdAsync(int id, int currentUserId, bool isAdmin)
    {
        var booking = await FindBookingWithIncludes().FirstOrDefaultAsync(b => b.Id == id)
            ?? throw new GraphQLException($"Бронирование с ID {id} не найдено");

        EnsureCanViewBooking(booking.UserId, currentUserId, isAdmin);

        if (booking.BookingItems.Count == 0)
            throw new GraphQLException("У бронирования нет связанных элементов оборудования");

        return booking;
    }

    public async Task<List<BookingEquipmentChangePayload>> GetBookingEquipmentHistoryAsync(int id, int currentUserId, bool isAdmin)
    {
        var ownerId = await db.Bookings.Where(booking => booking.Id == id)
            .Select(booking => (int?)booking.UserId).SingleOrDefaultAsync()
            ?? throw new GraphQLException($"Бронирование с ID {id} не найдено");
        EnsureCanViewBooking(ownerId, currentUserId, isAdmin);

        var changes = await db.BookingEquipmentChanges.AsNoTracking()
            .Where(change => change.BookingId == id)
            .OrderByDescending(change => change.Revision)
            .ToListAsync();

        return changes.Select(change =>
        {
            var equipment = JsonSerializer.Deserialize<EquipmentChangeDetails>(change.EquipmentJson)!;
            return new BookingEquipmentChangePayload(
                change.Revision, change.ActorName, change.ChangedAt, equipment.Added, equipment.Removed);
        }).ToList();
    }

    private static void EnsureCanViewBooking(int ownerId, int currentUserId, bool isAdmin)
    {
        if (!isAdmin && ownerId != currentUserId)
            throw new GraphQLException("Вы не можете просматривать чужое бронирование");
    }

    public async Task<List<Booking>> GetBookingsByUserAsync(int userId)
    {
        return await NewestFirst(FindBookingWithIncludes().Where(b => b.UserId == userId))
            .ToListAsync();
    }

    public async Task<List<Booking>> GetAllBookingsAsync()
    {
        return await NewestFirst(FindBookingWithIncludes()).ToListAsync();
    }

    private static IQueryable<Booking> NewestFirst(IQueryable<Booking> query) =>
        query.OrderByDescending(b => b.CreationTime).ThenByDescending(b => b.Id).Take(LegacyListLimit);

    public Task<BookingPagePayload> GetAllBookingsPageAsync(
        int page = 1,
        string? search = null,
        BookingStatus? status = null,
        bool oldestFirst = false) =>
        GetBookingsPageAsync(FindBookingWithIncludes(), page, search, status, oldestFirst);

    public Task<BookingPagePayload> GetBookingsPageByUserAsync(
        int userId,
        int page = 1,
        string? search = null,
        BookingStatus? status = null,
        bool oldestFirst = false) =>
        GetBookingsPageAsync(FindBookingWithIncludes().Where(b => b.UserId == userId),
            page, search, status, oldestFirst);

    // The usage history of one unit, most recent use first.
    public Task<BookingPagePayload> GetBookingsPageByEquipmentItemAsync(
        int eqItemId,
        int page = 1,
        string? search = null,
        BookingStatus? status = null) =>
        GetBookingsPageAsync(
            FindBookingWithIncludes().Where(b => b.BookingItems.Any(bi => bi.EqItemId == eqItemId)),
            page, search?.Trim().TrimStart('#'), status, oldestFirst: false,
            pageSize: EquipmentHistoryPageSize, orderByStartTime: true);

    private async Task<BookingPagePayload> GetBookingsPageAsync(
        IQueryable<Booking> query,
        int page,
        string? search,
        BookingStatus? status,
        bool oldestFirst,
        int pageSize = DefaultPageSize,
        bool orderByStartTime = false)
    {
        if (page < 1)
            throw new GraphQLException("Номер страницы должен быть больше 0");

        if (status.HasValue)
            query = query.Where(b => b.Status == status.Value);

        var searchTerm = search?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(searchTerm))
        {
            var searchId = int.TryParse(searchTerm, out var id) ? id : (int?)null;
            // Contains is translated to a literal substring search, including '%' and '_'.
            query = query.Where(b =>
                (searchId.HasValue && b.Id == searchId.Value) ||
                b.User.Name.ToLower().Contains(searchTerm) ||
                b.User.Login.ToLower().Contains(searchTerm) ||
                b.Reason.ToLower().Contains(searchTerm) ||
                b.BookingItems.Any(item => item.EqItem.EqModel.Name.ToLower().Contains(searchTerm)));
        }

        var totalCount = await query.CountAsync();
        var lastPage = totalCount == 0 ? 1 : (totalCount - 1) / pageSize + 1;
        page = Math.Min(page, lastPage);
        var orderedQuery = orderByStartTime
            ? query.OrderByDescending(b => b.StartTime).ThenByDescending(b => b.Id)
            : oldestFirst
                ? query.OrderBy(b => b.CreationTime).ThenBy(b => b.Id)
                : query.OrderByDescending(b => b.CreationTime).ThenByDescending(b => b.Id);
        var items = await orderedQuery.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return new BookingPagePayload(items, totalCount, page, pageSize);
    }

    public async Task<List<CalendarBookingPayload>> GetCalendarBookingsAsync(DateTime? start = null, DateTime? end = null)
    {
        // The calendar always asks for the weeks it shows. An open-ended request would return
        // every active booking with its owner's name, so it is refused.
        if (!start.HasValue || !end.HasValue)
            throw new GraphQLException("Укажите начало и конец периода календаря");
        if (start.Value >= end.Value)
            throw new GraphQLException("Начало периода должно быть раньше конца");
        if (end.Value - start.Value > TimeSpan.FromDays(CalendarMaxDays))
            throw new GraphQLException($"Период календаря не может превышать {CalendarMaxDays} дней");

        var query = db.Bookings.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Pending || b.Status == BookingStatus.Approved)
            .Where(b => b.StartTime < end.Value && b.EndTime > start.Value);

        return await query
            .OrderBy(b => b.StartTime)
            .Select(b => new CalendarBookingPayload(
                b.Id,
                b.User.Name,
                b.User.TelegramUsername,
                b.Reason,
                b.StartTime,
                b.EndTime,
                b.Status,
                b.BookingItems.OrderBy(item => item.Id)
                    .Select(item => new CalendarEquipmentPayload(
                        item.Id, item.EqItem.EqModel.Name, item.EqItem.InventoryNumber))
                    .ToList()))
            .ToListAsync();
    }

    public async Task<List<Booking>> GetBookingsByEquipmentItemAsync(int eqItemId)
    {
        return await NewestFirst(FindBookingWithIncludes()
                .Where(b => b.BookingItems.Any(bi => bi.EqItemId == eqItemId)))
            .ToListAsync();
    }

    public async Task<List<Booking>> GetBookingsByStatusAsync(BookingStatus status)
    {
        var bookings = await NewestFirst(FindBookingWithIncludes().Where(b => b.Status == status))
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

        var bookings = await NewestFirst(FindBookingWithIncludes()
                .Where(b => b.BookingItems.Any(bi => bi.EqItemId == eqItem.Id)))
            .ToListAsync();

        if (bookings.Count == 0)
            throw new GraphQLException(
                $"Нет бронирований для оборудования с инвентарным номером {inventoryNumber}");

        return bookings;
    }

    public async Task<Booking> ApproveBookingAsync(int bookingId, int expectedRevision, int actorUserId, string? adminComment = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await AcquireAdvisoryLockAsync(2, bookingId);

        var booking = await db.Bookings.FindAsync(bookingId)
            ?? throw new GraphQLException($"Бронирование с ID {bookingId} не найдено");

        await RequireDecisionActorAsync(actorUserId, requireAdmin: true);
        EnsureRevision(booking, expectedRevision);

        if (booking.Status != BookingStatus.Pending)
            throw new GraphQLException("Бронирование недоступно для обработки");

        var oldStatus = booking.Status;
        booking.Status = BookingStatus.Approved;
        booking.Revision++;
        if (!string.IsNullOrWhiteSpace(adminComment))
            booking.AdminComment = adminComment;

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        var updatedBooking = await GetBookingEntityByIdAsync(bookingId);
        await NotifyBookingStatusChangedAsync(updatedBooking, oldStatus, actorUserId);

        return updatedBooking;
    }

    public async Task<Booking> CompleteBookingAsync(int bookingId, int expectedRevision, int actorUserId)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await AcquireAdvisoryLockAsync(2, bookingId);

        var booking = await db.Bookings.FindAsync(bookingId)
            ?? throw new GraphQLException($"Бронирование с ID {bookingId} не найдено");

        await RequireDecisionActorAsync(actorUserId, requireAdmin: true);
        EnsureRevision(booking, expectedRevision);

        if (booking.Status != BookingStatus.Approved)
            throw new GraphQLException("Завершить можно только одобренное бронирование");

        var oldStatus = booking.Status;
        booking.Status = BookingStatus.Completed;
        booking.Revision++;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        var updatedBooking = await GetBookingEntityByIdAsync(bookingId);
        await NotifyBookingStatusChangedAsync(updatedBooking, oldStatus, actorUserId);

        return updatedBooking;
    }

    public async Task<Booking> CancelBookingAsync(int bookingId, int userId, bool isAdmin, int expectedRevision, string? adminComment = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await AcquireAdvisoryLockAsync(2, bookingId);

        var booking = await db.Bookings
            .Include(b => b.User)
            .FirstOrDefaultAsync(b => b.Id == bookingId)
            ?? throw new GraphQLException($"Бронирование с ID {bookingId} не найдено");

        await RequireDecisionActorAsync(userId, requireAdmin: isAdmin);
        EnsureRevision(booking, expectedRevision);
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
        booking.Revision++;

        if (isAdmin && !string.IsNullOrWhiteSpace(adminComment))
            booking.AdminComment = adminComment;

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        var updatedBooking = await GetBookingEntityByIdAsync(bookingId);
        await NotifyBookingStatusChangedAsync(updatedBooking, oldStatus, userId);

        return updatedBooking;
    }

    // Removes the booking and its items for good, unlike cancellation which keeps the record.
    public async Task<bool> DeleteBookingAsync(int bookingId, int expectedRevision, int actorUserId)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await AcquireAdvisoryLockAsync(2, bookingId);

        var booking = await db.Bookings
            .Include(b => b.BookingItems)
            .FirstOrDefaultAsync(b => b.Id == bookingId)
            ?? throw new GraphQLException($"Бронирование с ID {bookingId} не найдено");

        await RequireDecisionActorAsync(actorUserId, requireAdmin: true);
        EnsureRevision(booking, expectedRevision);

        // A cart editing this booking would otherwise keep a draft that can never be saved.
        var editingCarts = await db.Carts
            .Include(c => c.Items)
            .Where(c => c.EditingBookingId == bookingId)
            .ToListAsync();
        foreach (var cart in editingCarts)
        {
            db.CartItems.RemoveRange(cart.Items);
            cart.Reason = "";
            cart.StartTime = null;
            cart.EndTime = null;
            cart.Comment = null;
            cart.EditingBookingId = null;
            cart.UpdatedAt = DateTime.UtcNow;
        }

        db.BookingItems.RemoveRange(booking.BookingItems);
        db.Bookings.Remove(booking);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }

    public string GetWarningsJson(Booking booking)
    {
        return string.IsNullOrWhiteSpace(booking.WarningsJson) ? "{}" : booking.WarningsJson;
    }

    // Creation and editing share access checks, warnings and allocation. During editing,
    // the current booking is excluded from conflicts and administrators can grant Ronin equipment.
    private async Task<EquipmentAllocation> AllocateEquipmentAsync(
        CreateBookingInput input,
        User owner,
        bool isAdmin = false,
        int? excludedBookingId = null)
    {
        var warnings = new Dictionary<string, object>();
        if ((input.StartTime - DateTime.UtcNow).TotalDays < 3)
            warnings["invalidDate"] = "Бронирование создается меньше чем за 3 дня";

        var items = new List<BookingItem>();
        foreach (var requestedItem in NormalizeRequestedEquipment(input.Equipment))
        {
            await AcquireAdvisoryLockAsync(1, requestedItem.EqModelId);
            var model = await db.EqModels.FindAsync(requestedItem.EqModelId)
                ?? throw new GraphQLException($"Модель оборудования с ID {requestedItem.EqModelId} не найдена");

            var hasAccess = HasEquipmentAccess(owner.Role, model.Access);
            if (!isAdmin && !hasAccess && model.Access == EqAccess.Ronin)
                throw new GraphQLException(
                    $"У вас нет доступа к оборудованию {model.Name}. Требуется разрешение на Ronin");
            if (!hasAccess && model.Access == EqAccess.Osnova)
                warnings[$"missingOsnovaAccess_{model.Id}"] =
                    $"Для оборудования {model.Name} требуется доступ «Основа»";

            var selectedIds = requestedItem.SelectedItemIds ?? [];
            var selected = await db.EqItems.AsNoTracking()
                .Where(item => item.EqModelId == model.Id && item.ArchivedAt == null &&
                               selectedIds.Contains(item.Id))
                .ToListAsync();
            if (selected.Count != selectedIds.Count)
                throw new GraphQLException($"Выбранный экземпляр не найден или не относится к модели {model.Name}");

            var availableQuery = EquipmentAvailability.ForModel(
                db.EqItems, model.Id, input.StartTime, input.EndTime, excludedBookingId);
            var availableSelectedIds = await availableQuery
                .Where(item => selectedIds.Contains(item.Id)).Select(item => item.Id).ToListAsync();
            foreach (var selectedItem in selected)
            {
                if (!selectedItem.Operable)
                    throw new GraphQLException($"Экземпляр {selectedItem.InventoryNumber} ({model.Name}) неисправен. Выберите другой инвентарный номер.");
                if (!availableSelectedIds.Contains(selectedItem.Id))
                    throw new GraphQLException($"Экземпляр {selectedItem.InventoryNumber} ({model.Name}) занят в выбранный период. Выберите другой инвентарный номер.");
            }

            items.AddRange(selected.Select(item => new BookingItem
            {
                BookingId = excludedBookingId ?? 0,
                EqItemId = item.Id,
                StartDate = input.StartTime,
                EndDate = input.EndTime,
                IsReturned = false,
                IsExplicitlySelected = true
            }));
        }

        return new EquipmentAllocation(items, warnings);
    }

    private sealed record EquipmentAllocation(List<BookingItem> Items, Dictionary<string, object> Warnings);

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

    private async Task<User> EnsureCanBookAsync(int userId)
    {
        // Read current state even when an earlier request operation already tracked this user.
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId)
            ?? throw new GraphQLException("Пользователь не найден");
        if (user.Banned)
        {
            var message = "Вам запрещено бронировать оборудование";
            if (!string.IsNullOrWhiteSpace(user.BanReason))
                message += $". Причина блокировки: {user.BanReason}";
            throw new GraphQLException(message);
        }
        return user;
    }

    private static void ValidateBookingInput(CreateBookingInput input)
    {
        if (input.StartTime >= input.EndTime)
            throw new GraphQLException("Дата начала должна быть раньше даты окончания");
        if (string.IsNullOrWhiteSpace(input.Reason))
            throw new GraphQLException("Причина бронирования не может быть пустой");
        if (input.Equipment is null || input.Equipment.Count == 0)
            throw new GraphQLException("Не выбрано оборудование для бронирования");
    }

    private async Task RequireDecisionActorAsync(int userId, bool requireAdmin)
    {
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == userId);
        if (actor is null || (requireAdmin && (actor.Banned || actor.Role != UserRole.Admin)))
            throw new GraphQLException("У вас нет прав для этого действия");
    }

    private static void EnsureRevision(Booking booking, int expectedRevision)
    {
        if (expectedRevision < 1 || booking.Revision != expectedRevision)
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage("Бронирование изменилось. Обновите страницу и проверьте актуальные условия перед решением.")
                .SetCode("BOOKING_CHANGED").Build());
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
            if (item.EqModelId <= 0)
                throw new GraphQLException("ID модели оборудования должен быть больше 0");

            if (item.Quantity <= 0)
                throw new GraphQLException($"Выберите хотя бы один инвентарный номер для модели с ID {item.EqModelId}");

            var selected = item.SelectedItemIds ?? [];
            if (selected.Count != item.Quantity)
                throw new GraphQLException("Выберите инвентарные номера для каждого экземпляра оборудования");
            if (selected.Any(id => id <= 0) || selected.Distinct().Count() != selected.Count)
                throw new GraphQLException("Один инвентарный номер нельзя выбрать несколько раз");
        }

        return equipment
            .GroupBy(item => item.EqModelId)
            .OrderBy(group => group.Key)
            .Select(group => new CreateBookingEquipmentInput(
                group.Key,
                SumQuantity(group),
                MergeSelectedIds(group)))
            .ToList();

        static int SumQuantity(IGrouping<int, CreateBookingEquipmentInput> group)
        {
            var quantity = group.Sum(item => (long)item.Quantity);
            if (quantity > int.MaxValue)
                throw new GraphQLException($"Слишком большое количество для модели с ID {group.Key}");
            return (int)quantity;
        }

        static int[] MergeSelectedIds(IGrouping<int, CreateBookingEquipmentInput> group)
        {
            var ids = group.SelectMany(item => item.SelectedItemIds ?? []).ToArray();
            if (ids.Distinct().Count() != ids.Length)
                throw new GraphQLException("Один инвентарный номер нельзя выбрать несколько раз");
            return ids;
        }
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
    int EqModelId,
    int Quantity,
    IReadOnlyList<int>? SelectedItemIds = null
);
