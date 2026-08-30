using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using HotChocolate;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.Services;

public class CartService(AppDbContext db, BookingService bookingService)
{
    public async Task<Cart> GetOrCreateCartAsync(int userId)
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

    public async Task<Cart> SetCartDetailsAsync(int userId, UpdateCartDetailsInput input)
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
        return await GetOrCreateCartAsync(userId);
    }

    public async Task<Cart> AddCartItemAsync(int userId, int eqModelId, int quantity)
    {
        if (quantity <= 0)
            throw new GraphQLException("Количество должно быть больше 0");

        var cart = await GetCartTrackedAsync(userId);
        var eqModel = await db.EqModels.FindAsync(eqModelId)
            ?? throw new GraphQLException("Модель оборудования не найдена");

        var item = await db.CartItems.FirstOrDefaultAsync(i => i.CartId == cart.Id && i.EqModelId == eqModelId);
        if (item == null)
        {
            item = new CartItem
            {
                CartId = cart.Id,
                EqModelId = eqModel.Id,
                Quantity = quantity
            };
            db.CartItems.Add(item);
        }
        else
        {
            item.Quantity += quantity;
        }

        cart.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return await GetOrCreateCartAsync(userId);
    }

    public async Task<Cart> UpdateCartItemQuantityAsync(int userId, int eqModelId, int quantity)
    {
        var cart = await GetCartTrackedAsync(userId);
        var item = await db.CartItems.FirstOrDefaultAsync(i => i.CartId == cart.Id && i.EqModelId == eqModelId)
            ?? throw new GraphQLException("Позиция корзины не найдена");

        if (quantity <= 0)
            db.CartItems.Remove(item);
        else
            item.Quantity = quantity;

        cart.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return await GetOrCreateCartAsync(userId);
    }

    public async Task<Cart> RemoveCartItemAsync(int userId, int eqModelId)
    {
        var cart = await GetCartTrackedAsync(userId);
        var item = await db.CartItems.FirstOrDefaultAsync(i => i.CartId == cart.Id && i.EqModelId == eqModelId)
            ?? throw new GraphQLException("Позиция корзины не найдена");

        db.CartItems.Remove(item);
        cart.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return await GetOrCreateCartAsync(userId);
    }

    public async Task<bool> ClearCartAsync(int userId)
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

    public async Task<Cart> AddBookingItemsToCartAsync(int userId, int bookingId)
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
            var cartItem = cart.Items.FirstOrDefault(item => item.EqModelId == group.Key);
            if (cartItem == null)
            {
                db.CartItems.Add(new CartItem
                {
                    CartId = cart.Id,
                    EqModelId = group.Key,
                    Quantity = group.Count()
                });
            }
            else
            {
                cartItem.Quantity += group.Count();
            }
        }

        cart.EditingBookingId = null;
        cart.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return await GetOrCreateCartAsync(userId);
    }

    public async Task<Cart> PrepareBookingEditAsync(int userId, int bookingId, bool isAdmin)
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
                Quantity = group.Count()
            });
        }

        cart.Reason = booking.Reason;
        cart.StartTime = booking.StartTime;
        cart.EndTime = booking.EndTime;
        cart.Comment = booking.Comment;
        cart.EditingBookingId = booking.Id;
        cart.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return await GetOrCreateCartAsync(userId);
    }

    public async Task<Booking> CreateBookingFromCartAsync(int userId)
    {
        var cart = await GetOrCreateCartAsync(userId);
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
            cart.Items.Select(i => new CreateBookingEquipmentInput(i.EqModel.Name, i.Quantity)).ToList());

        var booking = await bookingService.CreateBookingAsync(input, userId);
        await ClearCartAsync(userId);
        return booking;
    }

    public async Task<Booking> UpdateBookingFromCartAsync(int userId, int bookingId, bool isAdmin)
    {
        var cart = await GetOrCreateCartAsync(userId);
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
            cart.Items.Select(i => new CreateBookingEquipmentInput(i.EqModel.Name, i.Quantity)).ToList());

        var booking = await bookingService.UpdateBookingAsync(bookingId, input, userId, isAdmin);
        await ClearCartAsync(userId);
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
