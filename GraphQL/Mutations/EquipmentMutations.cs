using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate.Authorization;

namespace GUtv_backend_dotnet.GraphQL.Mutations;

[ExtendObjectType(typeof(Mutation))]
public class EquipmentMutations
{
    [Authorize(Roles = ["Admin"])]
    public Task<EqModel> CreateEquipmentModel(
        CreateEqModelInput input,
        EquipmentService equipmentService) =>
        equipmentService.CreateModelAsync(input);

    [Authorize(Roles = ["Admin"])]
    public Task<EqModel> UpdateEquipmentModel(
        int id,
        CreateEqModelInput input,
        EquipmentService equipmentService) =>
        equipmentService.UpdateModelAsync(id, input);

    [Authorize(Roles = ["Admin"])]
    public Task<EqModel> UpdateEquipmentModelProperties(
        int id,
        UpdateEqModelPropertiesInput input,
        EquipmentService equipmentService) =>
        equipmentService.UpdateModelPropertiesAsync(id, input);

    [Authorize(Roles = ["Admin"])]
    public Task<bool> DeleteEquipmentModel(int id, EquipmentService equipmentService) =>
        equipmentService.DeleteModelAsync(id);

    [Authorize(Roles = ["Admin"])]
    public Task<EqItem> CreateEquipmentItem(int equipmentModelId, EquipmentService equipmentService) =>
        equipmentService.CreateItemAsync(equipmentModelId);

    [Authorize(Roles = ["Admin"])]
    public Task<EqItem> ToggleEquipmentItemAvailability(int id, EquipmentService equipmentService) =>
        equipmentService.ToggleItemAvailabilityAsync(id);

    [Authorize(Roles = ["Admin"])]
    public Task<bool> DeleteEquipmentItem(int id, EquipmentService equipmentService) =>
        equipmentService.DeleteItemAsync(id);
}
