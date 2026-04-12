using System.Security.Claims;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate.Authorization;

namespace GUtv_backend_dotnet.GraphQL.Queries;

[ExtendObjectType(typeof(Query))]
public class EquipmentQueries
{
    public Task<List<EqModel>> GetAllEquipmentModels(EquipmentService equipmentService) =>
        equipmentService.GetAllModelsAsync();

    public Task<EqModel> GetEquipmentModelById(int id, EquipmentService equipmentService) =>
        equipmentService.GetModelByIdAsync(id);

    public Task<List<EqModel>> GetEquipmentModelsByName(string name, EquipmentService equipmentService) =>
        equipmentService.GetModelsByNameAsync(name);

    public Task<List<EqModel>> GetEquipmentModelsByCategory(EqCategory category, EquipmentService equipmentService) =>
        equipmentService.GetModelsByCategoryAsync(category);

    public Task<List<EqModelWithItemsPayload>> GetEquipmentModelsWithItems(EquipmentService equipmentService) =>
        equipmentService.GetModelsWithItemsAsync();

    [Authorize]
    public Task<List<EqModel>> GetAvailableEquipmentModelsToMe(
        ClaimsPrincipal claimsPrincipal,
        EquipmentService equipmentService)
    {
        var userId = equipmentService.GetRequiredUserId(claimsPrincipal);
        return equipmentService.GetAvailableToUserAsync(userId);
    }

    public Task<List<EqItem>> GetAvailableEquipmentItemsByModel(
        int modelId,
        DateTime start,
        DateTime end,
        EquipmentService equipmentService) =>
        equipmentService.GetAvailableItemsByModelAsync(modelId, start, end);

    public Task<List<EqItem>> GetAllEquipmentItems(EquipmentService equipmentService) =>
        equipmentService.GetAllItemsAsync();

    public Task<EqItem> GetEquipmentItemById(int id, EquipmentService equipmentService) =>
        equipmentService.GetItemByIdAsync(id);

    public Task<List<EqItem>> GetEquipmentItemsByModel(int modelId, EquipmentService equipmentService) =>
        equipmentService.GetItemsByModelAsync(modelId);
}
