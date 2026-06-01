using System.Security.Claims;
using System.Text.Json;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using HotChocolate;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.Services;

public class EquipmentService(AppDbContext db)
{
    public async Task<EqModel> CreateModelAsync(CreateEqModelInput input)
    {
        ValidateModelInput(input);

        var normalizedName = input.Name.Trim();
        var exists = await db.EqModels.AnyAsync(m => EF.Functions.ILike(m.Name, normalizedName));
        if (exists)
            throw new GraphQLException("Оборудование с таким названием уже существует");

        var model = new EqModel
        {
            Name = normalizedName,
            Description = input.Description.Trim(),
            Category = input.Category,
            Access = ResolveAccess(normalizedName, input.Osnova),
            AttributesJson = NormalizeJson(input.AttributesJson)
        };

        db.EqModels.Add(model);
        await db.SaveChangesAsync();
        return model;
    }

    public async Task<List<EqModel>> GetAllModelsAsync()
    {
        return await db.EqModels
            .AsNoTracking()
            .Include(m => m.Photos.OrderBy(p => p.Order))
            .ToListAsync();
    }

    public async Task<EqModel> GetModelByIdAsync(int id)
    {
        if (id <= 0)
            throw new GraphQLException("Некорректный ID");

        return await db.EqModels
            .AsNoTracking()
            .Include(m => m.Photos.OrderBy(p => p.Order))
            .FirstOrDefaultAsync(m => m.Id == id)
            ?? throw new GraphQLException($"Модель оборудования с ID {id} не найдена");
    }

    public async Task<List<EqModel>> GetModelsByNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new GraphQLException("Название не может быть пустым");

        var models = await db.EqModels
            .AsNoTracking()
            .Include(m => m.Photos.OrderBy(p => p.Order))
            .Where(m => EF.Functions.ILike(m.Name, $"%{name.Trim()}%"))
            .ToListAsync();

        if (models.Count == 0)
            throw new GraphQLException($"Оборудование с названием '{name}' не найдено");

        return models;
    }

    public async Task<List<EqModel>> GetModelsByCategoryAsync(EqCategory category)
    {
        var models = await db.EqModels
            .AsNoTracking()
            .Include(m => m.Photos.OrderBy(p => p.Order))
            .Where(m => m.Category == category)
            .ToListAsync();

        if (models.Count == 0)
            throw new GraphQLException($"Оборудование категории {category} не найдено");

        return models;
    }

    public async Task<List<EqModel>> GetAvailableToUserAsync(int userId)
    {
        var user = await db.Users.FindAsync(userId)
            ?? throw new GraphQLException("Пользователь не найден");

        IQueryable<EqModel> query = db.EqModels
            .AsNoTracking()
            .Include(m => m.Photos.OrderBy(p => p.Order));

        query = user.Role switch
        {
            UserRole.Admin or UserRole.Ronin => query,
            UserRole.Osnova => query.Where(m =>
                m.Access == EqAccess.User || m.Access == EqAccess.Osnova),
            UserRole.User => query.Where(m => m.Access == EqAccess.User),
            _ => throw new GraphQLException("Неизвестная роль пользователя")
        };

        return await query.ToListAsync();
    }

    public async Task<List<EqModelWithItemsPayload>> GetModelsWithItemsAsync()
    {
        var models = await db.EqModels
            .AsNoTracking()
            .Include(m => m.Photos.OrderBy(p => p.Order))
            .Include(m => m.EqItems.OrderBy(i => i.Id))
            .ToListAsync();

        return models
            .Select(m => new EqModelWithItemsPayload(
                m.Id,
                m.Name,
                m.Description,
                m.Category,
                m.Access,
                m.AttributesJson,
                m.Photos,
                m.EqItems))
            .ToList();
    }

    public async Task<EqModel> UpdateModelAsync(int id, CreateEqModelInput input)
    {
        if (id <= 0)
            throw new GraphQLException("ID должен быть положительным");

        ValidateModelInput(input);

        var model = await db.EqModels.FindAsync(id)
            ?? throw new GraphQLException($"Модель оборудования с ID {id} не найдена");

        var normalizedName = input.Name.Trim();
        var nameExists = await db.EqModels.AnyAsync(m =>
            m.Id != id && EF.Functions.ILike(m.Name, normalizedName));
        if (nameExists)
            throw new GraphQLException("Оборудование с таким названием уже существует");

        model.Name = normalizedName;
        model.Description = input.Description.Trim();
        model.Category = input.Category;
        model.Access = ResolveAccess(normalizedName, input.Osnova);
        model.AttributesJson = NormalizeJson(input.AttributesJson);

        await db.SaveChangesAsync();
        return model;
    }

    public async Task<EqModel> UpdateModelPropertiesAsync(int id, UpdateEqModelPropertiesInput input)
    {
        if (id <= 0)
            throw new GraphQLException("ID должен быть положительным");

        if (string.IsNullOrWhiteSpace(input.Name))
            throw new GraphQLException("Название не может быть пустым");

        var model = await db.EqModels.FindAsync(id)
            ?? throw new GraphQLException($"Модель оборудования с ID {id} не найдена");

        var normalizedName = input.Name.Trim();
        var nameExists = await db.EqModels.AnyAsync(m =>
            m.Id != id && EF.Functions.ILike(m.Name, normalizedName));
        if (nameExists)
            throw new GraphQLException("Оборудование с таким названием уже существует");

        model.Name = normalizedName;
        model.Description = input.Description?.Trim() ?? string.Empty;
        if (input.AttributesJson is not null)
            model.AttributesJson = NormalizeJson(input.AttributesJson);

        await db.SaveChangesAsync();

        return await db.EqModels
            .AsNoTracking()
            .Include(m => m.Photos.OrderBy(p => p.Order))
            .FirstAsync(m => m.Id == id);
    }

    public async Task<bool> DeleteModelAsync(int id)
    {
        var model = await db.EqModels.FindAsync(id)
            ?? throw new GraphQLException($"Модель оборудования с ID {id} не найдена");

        db.EqModels.Remove(model);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<EqItem> CreateItemAsync(int eqModelId)
    {
        var model = await db.EqModels.FirstOrDefaultAsync(m => m.Id == eqModelId)
            ?? throw new GraphQLException("Модель оборудования не найдена");

        var categoryCode = (int)model.Category;

        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);

        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"EqModels\" WHERE \"Id\" = {0} FOR UPDATE",
                eqModelId);

            var itemNumbers = await db.EqItems
                .Where(i => i.EqModelId == eqModelId)
                .Select(i => i.InventoryNumber)
                .ToListAsync();

            var nextNumber = itemNumbers.Count == 0
                ? 1
                : itemNumbers.Max(GetInventorySequence) + 1;

            var item = new EqItem
            {
                EqModelId = eqModelId,
                EqModel = model,
                InventoryNumber = $"{categoryCode}-{eqModelId:D3}-{nextNumber:D2}",
                Operable = true
            };

            db.EqItems.Add(item);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            return await db.EqItems
                .AsNoTracking()
                .Include(i => i.EqModel)
                .FirstAsync(i => i.Id == item.Id);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<List<EqItem>> GetAllItemsAsync()
    {
        return await db.EqItems
            .AsNoTracking()
            .Include(i => i.EqModel)
            .ToListAsync();
    }

    public async Task<EqItem> GetItemByIdAsync(int id)
    {
        if (id <= 0)
            throw new GraphQLException("Некорректный ID");

        return await db.EqItems
            .AsNoTracking()
            .Include(i => i.EqModel)
            .FirstOrDefaultAsync(i => i.Id == id)
            ?? throw new GraphQLException($"Экземпляр оборудования с ID {id} не найден");
    }

    public async Task<List<EqItem>> GetItemsByModelAsync(int eqModelId)
    {
        var modelExists = await db.EqModels.AnyAsync(m => m.Id == eqModelId);
        if (!modelExists)
            throw new GraphQLException($"Модель оборудования с ID {eqModelId} не найдена");

        var items = await db.EqItems
            .AsNoTracking()
            .Include(i => i.EqModel)
            .Where(i => i.EqModelId == eqModelId)
            .ToListAsync();

        if (items.Count == 0)
            throw new GraphQLException($"Нет экземпляров для модели {eqModelId}");

        return items;
    }

    public async Task<List<EqItem>> GetAvailableItemsByModelAsync(int eqModelId, DateTime start, DateTime end)
    {
        if (eqModelId <= 0)
            throw new GraphQLException("Некорректный ID модели");

        if (start >= end)
            throw new GraphQLException("Дата начала должна быть раньше даты окончания");

        var modelExists = await db.EqModels.AnyAsync(m => m.Id == eqModelId);
        if (!modelExists)
            throw new GraphQLException($"Модель оборудования с ID {eqModelId} не найдена");

        return await db.EqItems
            .AsNoTracking()
            .Include(i => i.EqModel)
            .Where(i => i.EqModelId == eqModelId)
            .Where(i => i.Operable)
            .Where(i => !i.BookingItems.Any(bi =>
                (bi.Booking.Status == BookingStatus.Pending ||
                 bi.Booking.Status == BookingStatus.Approved) &&
                start < bi.EndDate && end > bi.StartDate))
            .ToListAsync();
    }

    public async Task<EqItem> ToggleItemAvailabilityAsync(int id)
    {
        var item = await db.EqItems
            .Include(i => i.EqModel)
            .FirstOrDefaultAsync(i => i.Id == id)
            ?? throw new GraphQLException($"Экземпляр оборудования с ID {id} не найден");

        item.Operable = !item.Operable;
        await db.SaveChangesAsync();
        return item;
    }

    public async Task<bool> DeleteItemAsync(int id)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);

        try
        {
            var item = await db.EqItems
                .Include(i => i.EqModel)
                .FirstOrDefaultAsync(i => i.Id == id)
                ?? throw new GraphQLException($"Экземпляр оборудования с ID {id} не найден");

            await db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"EqModels\" WHERE \"Id\" = {0} FOR UPDATE",
                item.EqModelId);

            var modelItems = await db.EqItems
                .Where(i => i.EqModelId == item.EqModelId)
                .Select(i => new { i.Id, i.InventoryNumber })
                .ToListAsync();

            var lastItem = modelItems
                .OrderByDescending(i => GetInventorySequence(i.InventoryNumber))
                .ThenByDescending(i => i.Id)
                .FirstOrDefault();

            if (lastItem?.Id != item.Id)
                throw new GraphQLException(
                    $"Удалять можно только последний экземпляр модели. Сейчас последний: {lastItem?.InventoryNumber ?? "не найден"}");

            db.EqItems.Remove(item);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return true;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public int GetRequiredUserId(ClaimsPrincipal? user)
    {
        var value = user?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user?.FindFirstValue("sub");

        if (!int.TryParse(value, out var userId))
            throw new GraphQLException("Не удалось получить идентификатор пользователя из токена");

        return userId;
    }

    private static EqAccess ResolveAccess(string name, bool osnova)
    {
        if (name.Contains("Ronin", StringComparison.OrdinalIgnoreCase))
            return EqAccess.Ronin;

        if (osnova)
            return EqAccess.Osnova;

        return EqAccess.User;
    }

    private static int GetInventorySequence(string inventoryNumber)
    {
        var parts = inventoryNumber.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && int.TryParse(parts[^1], out var sequence)
            ? sequence
            : 0;
    }

    private static string NormalizeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "{}";

        var trimmed = json.Trim();
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new GraphQLException("JSON свойства оборудования должны быть объектом");

            return trimmed;
        }
        catch (GraphQLException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new GraphQLException("JSON свойства оборудования заполнены некорректно");
        }
    }

    private static void ValidateModelInput(CreateEqModelInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new GraphQLException("Название не может быть пустым");

        if (string.IsNullOrWhiteSpace(input.Description))
            throw new GraphQLException("Описание не может быть пустым");
    }
}

public record CreateEqModelInput(
    string Name,
    string Description,
    EqCategory Category,
    string? AttributesJson,
    bool Osnova = false
);

public record UpdateEqModelPropertiesInput(
    string Name,
    string? Description,
    string? AttributesJson
);

public record EqModelWithItemsPayload(
    int Id,
    string Name,
    string Description,
    EqCategory Category,
    EqAccess Access,
    string AttributesJson,
    IReadOnlyList<EqPhoto> Photos,
    IReadOnlyList<EqItem> Items
);
