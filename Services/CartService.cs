using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using HotChocolate;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.Services;

public class CartService(AppDbContext db, BookingService bookingService)
{
    public Task<Cart> GetOrCreateCartAsync(int userId) =>
        WithCartLockAsync(userId, () => GetOrCreateCartCoreAsync(userId));

    public Task<Cart> SetCartDetailsAsync(int userId, UpdateCartDetailsInput input) =>
        WithCartLockAsync(userId, () => SetCartDetailsCoreAsync(userId, input));

    public Task<Cart> AddCartItemAsync(int userId, int eqModelId, int quantity, int? selectedItemId = null) =>
        WithCartLockAsync(userId, () => AddCartItemCoreAsync(userId, eqModelId, quantity, selectedItemId));

    public Task<Cart> SetCartItemSelectionAsync(int userId, int eqModelId, int[] selectedItemIds) =>
        WithCartLockAsync(userId, () => SetCartItemSelectionCoreAsync(userId, eqModelId, selectedItemIds));

    public async Task<List<CartEquipmentOption>> GetEquipmentOptionsAsync(
        int userId, int eqModelId, DateTime? start, DateTime? end)
    {
        if (start.HasValue != end.HasValue || (start.HasValue && start >= end))
            throw new GraphQLException("Укажите корректный период бронирования");

        var cart = await db.Carts.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == userId);
        // Editing may reuse its own assignments. The edit target is set only after checking access.
        var excludedBookingId = cart?.EditingBookingId;
        var options = await db.EqItems.AsNoTracking().Where(item => item.EqModelId == eqModelId)
            .OrderBy(item => item.InventoryNumber).ThenBy(item => item.Id)
            .Select(item => new { item.Id, item.InventoryNumber, item.Operable }).ToListAsync();
        var availableIds = start.HasValue
            ? await EquipmentAvailability.ForModel(db.EqItems, eqModelId, start.Value, end!.Value, excludedBookingId)
                .Select(item => item.Id).ToListAsync()
            : null;
        return options.Select(item => new CartEquipmentOption(item.Id, item.InventoryNumber, item.Operable,
            availableIds is null ? null : availableIds.Contains(item.Id))).ToList();
    }

    public Task<Cart> UpdateCartItemQuantityAsync(int userId, int eqModelId, int quantity) =>
        WithCartLockAsync(userId, () => UpdateCartItemQuantityCoreAsync(userId, eqModelId, quantity));

    public Task<Cart> RemoveCartItemAsync(int userId, int eqModelId) =>
        WithCartLockAsync(userId, () => RemoveCartItemCoreAsync(userId, eqModelId));

    public Task<bool> ClearCartAsync(int userId) =>
        WithCartLockAsync(userId, () => ClearCartCoreAsync(userId));

    public Task<Cart> AddBookingItemsToCartAsync(int userId, int bookingId) =>
        WithCartLockAsync(userId, () => AddBookingItemsToCartCoreAsync(userId, bookingId));

    public Task<Cart> PrepareBookingEditAsync(int userId, int bookingId, bool isAdmin) =>
        WithCartLockAsync(userId, () => PrepareBookingEditCoreAsync(userId, bookingId, isAdmin));

    public async Task<Booking> CreateBookingFromCartAsync(int userId)
    {
        var booking = await WithCartLockAsync(userId, () => CreateBookingFromCartCoreAsync(userId));
        await bookingService.NotifyBookingCreatedAsync(booking);
        return booking;
    }

    public async Task<Booking> UpdateBookingFromCartAsync(int userId, int bookingId, bool isAdmin)
    {
        var booking = await WithCartLockAsync(userId, () => UpdateBookingFromCartCoreAsync(userId, bookingId, isAdmin));
        await bookingService.NotifyBookingUpdatedAsync(booking, userId);
        return booking;
    }

    private async Task<T> WithCartLockAsync<T>(int userId, Func<Task<T>> operation)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Lock by user, even when their cart does not exist yet. This works across backend instances.
        // Lock order is cart -> booking -> equipment; booking operations never acquire cart locks.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(3, {userId})");

        // A GraphQL mutation may reuse this context for several root fields. Reload cart state
        // after taking the lock instead of retaining an earlier field's tracked snapshot.
        foreach (var entry in db.ChangeTracker.Entries()
                     .Where(entry => entry.Entity is Cart or CartItem).ToList())
            entry.State = EntityState.Detached;

        try
        {
            var result = await operation();
            await transaction.CommitAsync();
            return result;
        }
        catch
        {
            await transaction.RollbackAsync();
            // Do not let a later GraphQL field save entities left over from a rolled-back checkout.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<Cart> GetOrCreateCartCoreAsync(int userId)
    {
        var cart = await db.Carts
            .Include(c => c.Items)
            .ThenInclude(i => i.EqModel)
            .FirstOrDefaultAsync(c => c.UserId == userId);

        if (cart != null)
            return cart;

        cart = new Cart { UserId = userId, UpdatedAt = DateTime.UtcNow };
        db.Carts.Add(cart);
        await db.SaveChangesAsync();

        return await db.Carts
            .Include(c => c.Items)
            .ThenInclude(i => i.EqModel)
            .FirstAsync(c => c.Id == cart.Id);
    }

    private async Task<Cart> SetCartDetailsCoreAsync(int userId, UpdateCartDetailsInput input)
    {
        if (input.StartTime.HasValue && input.EndTime.HasValue && input.StartTime >= input.EndTime)
            throw new GraphQLException("Дата начала должна быть раньше даты окончания");

        var cart = await GetCartTrackedAsync(userId);
        cart.Reason = input.Reason?.Trim() ?? cart.Reason;
        cart.StartTime = input.StartTime ?? cart.StartTime;
        cart.EndTime = input.EndTime ?? cart.EndTime;
        cart.Comment = input.Comment ?? cart.Comment;
        cart.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return await GetOrCreateCartCoreAsync(userId);
    }

    private async Task<Cart> AddCartItemCoreAsync(int userId, int eqModelId, int quantity, int? selectedItemId)
    {
        if (quantity <= 0)
            throw new GraphQLException("Количество должно быть больше 0");

        var cart = await GetCartTrackedAsync(userId);
        var eqModel = await db.EqModels.FindAsync(eqModelId)
            ?? throw new GraphQLException("Модель оборудования не найдена");

        var item = await db.CartItems.FirstOrDefaultAsync(i => i.CartId == cart.Id && i.EqModelId == eqModelId);
        var selectedIds = (item?.SelectedItemIds ?? []).Concat(selectedItemId.HasValue ? [selectedItemId.Value] : Array.Empty<int>()).ToArray();
        await ValidateSelectionAsync(eqModelId, selectedIds.Length, selectedIds);
        if (item == null)
        {
            item = new CartItem
            {
                CartId = cart.Id,
                EqModelId = eqModel.Id,
                Quantity = selectedIds.Length,
                SelectedItemIds = selectedIds
            };
            db.CartItems.Add(item);
        }
        else
        {
            item.Quantity = selectedIds.Length;
            item.SelectedItemIds = selectedIds;
        }

        cart.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return await GetOrCreateCartCoreAsync(userId);
    }

    private async Task<Cart> SetCartItemSelectionCoreAsync(int userId, int eqModelId, int[] selectedItemIds)
    {
        var cart = await GetCartTrackedAsync(userId);
        var item = cart.Items.FirstOrDefault(item => item.EqModelId == eqModelId);
        await ValidateSelectionAsync(eqModelId, selectedItemIds.Length, selectedItemIds);
        if (item is null)
        {
            if (selectedItemIds.Length == 0)
                throw new GraphQLException("Выберите хотя бы один инвентарный номер");
            item = new CartItem { CartId = cart.Id, EqModelId = eqModelId };
            db.CartItems.Add(item);
        }
        item.Quantity = selectedItemIds.Length;
        item.SelectedItemIds = selectedItemIds.ToArray();
        cart.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return await GetOrCreateCartCoreAsync(userId);
    }

    private async Task ValidateSelectionAsync(int eqModelId, int quantity, int[] selectedIds)
    {
        if (selectedIds.Length > quantity || selectedIds.Any(id => id <= 0) || selectedIds.Distinct().Count() != selectedIds.Length)
            throw new GraphQLException("Выберите разные инвентарные номера, не больше указанного количества оборудования");
        if (selectedIds.Length == 0) return;
        var items = await db.EqItems.AsNoTracking()
            .Where(item => item.EqModelId == eqModelId && selectedIds.Contains(item.Id)).ToListAsync();
        if (items.Count != selectedIds.Length)
            throw new GraphQLException("Выбранный экземпляр не найден или не относится к этой модели");
        var faulty = items.FirstOrDefault(item => !item.Operable);
        if (faulty is not null)
            throw new GraphQLException($"Экземпляр {faulty.InventoryNumber} неисправен");
    }

    private async Task<Cart> UpdateCartItemQuantityCoreAsync(int userId, int eqModelId, int quantity)
    {
        var cart = await GetCartTrackedAsync(userId);
        var item = await db.CartItems.FirstOrDefaultAsync(i => i.CartId == cart.Id && i.EqModelId == eqModelId)
            ?? throw new GraphQLException("Позиция корзины не найдена");

        if (quantity <= 0)
            db.CartItems.Remove(item);
        else
        {
            if (quantity != item.SelectedItemIds.Length)
                throw new GraphQLException("Количество определяется выбранными инвентарными номерами");
            item.Quantity = item.SelectedItemIds.Length;
        }

        cart.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return await GetOrCreateCartCoreAsync(userId);
    }

    private async Task<Cart> RemoveCartItemCoreAsync(int userId, int eqModelId)
    {
        var cart = await GetCartTrackedAsync(userId);
        var item = await db.CartItems.FirstOrDefaultAsync(i => i.CartId == cart.Id && i.EqModelId == eqModelId)
            ?? throw new GraphQLException("Позиция корзины не найдена");

        db.CartItems.Remove(item);
        cart.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return await GetOrCreateCartCoreAsync(userId);
    }

    private async Task<bool> ClearCartCoreAsync(int userId)
    {
        var cart = await GetCartTrackedAsync(userId);

        db.CartItems.RemoveRange(db.CartItems.Where(i => i.CartId == cart.Id));
        cart.Reason = "";
        cart.StartTime = null;
        cart.EndTime = null;
        cart.Comment = null;
        cart.EditingBookingId = null;
        cart.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return true;
    }

    private async Task<Cart> AddBookingItemsToCartCoreAsync(int userId, int bookingId)
    {
        var booking = await GetOwnedBookingAsync(userId, bookingId);
        var cart = await GetCartTrackedAsync(userId);

        if (cart.EditingBookingId.HasValue)
        {
            db.CartItems.RemoveRange(cart.Items);
            cart.Items.Clear();
            cart.Reason = "";
            cart.StartTime = null;
            cart.EndTime = null;
            cart.Comment = null;
        }

        foreach (var group in booking.BookingItems.GroupBy(item => item.EqItem.EqModelId))
        {
            var selectedIds = group.Select(item => item.EqItemId).ToArray();
            var cartItem = cart.Items.FirstOrDefault(item => item.EqModelId == group.Key);
            if (cartItem == null)
            {
                db.CartItems.Add(new CartItem
                {
                    CartId = cart.Id,
                    EqModelId = group.Key,
                    Quantity = group.Count(),
                    SelectedItemIds = selectedIds
                });
            }
            else
            {
                var combinedIds = cartItem.SelectedItemIds.Concat(selectedIds).ToArray();
                if (combinedIds.Distinct().Count() != combinedIds.Length)
                    throw new GraphQLException("Этот инвентарный номер уже выбран в корзине");
                cartItem.Quantity = combinedIds.Length;
                cartItem.SelectedItemIds = combinedIds;
            }
        }

        cart.EditingBookingId = null;
        cart.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return await GetOrCreateCartCoreAsync(userId);
    }

    private async Task<Cart> PrepareBookingEditCoreAsync(int userId, int bookingId, bool isAdmin)
    {
        var booking = await GetBookingForCartAsync(
            userId,
            bookingId,
            requireEditable: true,
            allowAdmin: isAdmin);
        var cart = await GetCartTrackedAsync(userId);

        db.CartItems.RemoveRange(cart.Items);
        foreach (var group in booking.BookingItems.GroupBy(item => item.EqItem.EqModelId))
        {
            db.CartItems.Add(new CartItem
            {
                CartId = cart.Id,
                EqModelId = group.Key,
                Quantity = group.Count(),
                SelectedItemIds = group.Select(item => item.EqItemId).ToArray()
            });
        }

        cart.Reason = booking.Reason;
        cart.StartTime = booking.StartTime;
        cart.EndTime = booking.EndTime;
        cart.Comment = booking.Comment;
        cart.EditingBookingId = booking.Id;
        cart.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return await GetOrCreateCartCoreAsync(userId);
    }

    private async Task<Booking> CreateBookingFromCartCoreAsync(int userId)
    {
        var cart = await GetOrCreateCartCoreAsync(userId);
        if (cart.EditingBookingId.HasValue)
            throw new GraphQLException("Корзина находится в режиме редактирования бронирования");

        if (cart.Items.Count == 0)
            throw new GraphQLException("Корзина пуста");

        if (string.IsNullOrWhiteSpace(cart.Reason))
            throw new GraphQLException("В корзине не указана причина бронирования");

        if (!cart.StartTime.HasValue || !cart.EndTime.HasValue)
            throw new GraphQLException("В корзине не указаны даты бронирования");

        var input = new CreateBookingInput(
            cart.Reason,
            cart.StartTime.Value,
            cart.EndTime.Value,
            cart.Comment,
            cart.Items.Select(i => new CreateBookingEquipmentInput(i.EqModelId, i.Quantity, i.SelectedItemIds)).ToList());

        var booking = await bookingService.CreateBookingInTransactionAsync(input, userId);
        await ClearCartCoreAsync(userId);
        return booking;
    }

    private async Task<Booking> UpdateBookingFromCartCoreAsync(int userId, int bookingId, bool isAdmin)
    {
        var cart = await GetOrCreateCartCoreAsync(userId);
        if (cart.EditingBookingId != bookingId)
            throw new GraphQLException("Корзина не подготовлена для изменения этого бронирования");

        if (cart.Items.Count == 0)
            throw new GraphQLException("Бронирование не может быть пустым");

        if (string.IsNullOrWhiteSpace(cart.Reason))
            throw new GraphQLException("В корзине не указана причина бронирования");

        if (!cart.StartTime.HasValue || !cart.EndTime.HasValue)
            throw new GraphQLException("В корзине не указаны даты бронирования");

        var input = new CreateBookingInput(
            cart.Reason,
            cart.StartTime.Value,
            cart.EndTime.Value,
            cart.Comment,
            cart.Items.Select(i => new CreateBookingEquipmentInput(i.EqModelId, i.Quantity, i.SelectedItemIds)).ToList());

        var booking = await bookingService.UpdateBookingInTransactionAsync(bookingId, input, userId, isAdmin);
        await ClearCartCoreAsync(userId);
        return booking;
    }

    private async Task<Booking> GetOwnedBookingAsync(int userId, int bookingId)
    {
        return await GetBookingForCartAsync(userId, bookingId);
    }

    private async Task<Booking> GetBookingForCartAsync(
        int userId,
        int bookingId,
        bool requireEditable = false,
        bool allowAdmin = false)
    {
        var booking = await db.Bookings
            .AsNoTracking()
            .Include(b => b.BookingItems)
            .ThenInclude(item => item.EqItem)
            .FirstOrDefaultAsync(b => b.Id == bookingId)
            ?? throw new GraphQLException($"Бронирование с ID {bookingId} не найдено");

        if (booking.UserId != userId && !allowAdmin)
            throw new GraphQLException("Вы не можете использовать чужое бронирование");

        if (booking.BookingItems.Count == 0)
            throw new GraphQLException("В бронировании нет оборудования");

        if (requireEditable && !allowAdmin && booking.Status is not (BookingStatus.Pending or BookingStatus.Approved))
            throw new GraphQLException("Изменить можно только ожидающее или одобренное бронирование");

        return booking;
    }

    private async Task<Cart> GetCartTrackedAsync(int userId)
    {
        var cart = await db.Carts
            .Include(c => c.Items)
            .ThenInclude(i => i.EqModel)
            .FirstOrDefaultAsync(c => c.UserId == userId);

        if (cart != null)
            return cart;

        cart = new Cart { UserId = userId, UpdatedAt = DateTime.UtcNow };
        db.Carts.Add(cart);
        await db.SaveChangesAsync();
        return cart;
    }
}

public record UpdateCartDetailsInput(
    string? Reason,
    DateTime? StartTime,
    DateTime? EndTime,
    string? Comment
);

public record CartEquipmentOption(int Id, string InventoryNumber, bool Operable, bool? Available);
