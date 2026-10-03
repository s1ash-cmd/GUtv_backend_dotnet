using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.Tests;

public class EquipmentItemsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ExistingModelReturnsItsItemsIncludingAnEmptyCollection(int itemCount)
    {
        await using var db = CreateContext();
        var model = new EqModel { Id = 1, Name = "Camera" };
        db.EqModels.Add(model);
        if (itemCount > 0)
            db.EqItems.Add(new EqItem { Id = 1, EqModel = model, InventoryNumber = "CAM-1" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var items = await new EquipmentService(db).GetItemsByModelAsync(model.Id);

        Assert.Equal(itemCount, items.Count);
        Assert.All(items, item => Assert.Equal("Camera", item.EqModel.Name));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task MissingModelIsStillRejectedInsteadOfBeingReportedAsEmpty()
    {
        await using var db = CreateContext();

        var error = await Assert.ThrowsAsync<GraphQLException>(() =>
            new EquipmentService(db).GetItemsByModelAsync(1));

        Assert.Contains(error.Errors, item => item.Message == "Модель оборудования с ID 1 не найдена");
    }

    private static AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
