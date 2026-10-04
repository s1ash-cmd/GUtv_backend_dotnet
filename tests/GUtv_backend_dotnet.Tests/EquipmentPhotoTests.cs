using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Services;
using HotChocolate;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace GUtv_backend_dotnet.Tests;

public class EquipmentPhotoTests
{
    [Fact]
    public async Task UploadPersistsOrderedPhotosAndPreservesEquipmentProportions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Service.UploadAsync(1, await PhotoAsync());
        var second = await fixture.Service.UploadAsync(1, await PhotoAsync());
        var model = await new EquipmentService(fixture.Db).GetModelByIdAsync(1);
        Assert.Equal(new[] { first.Id, second.Id }, model.Photos.Select(p => p.Id));
        Assert.Equal(new[] { 0, 1 }, model.Photos.Select(p => p.Order));
        using var image = await Image.LoadAsync(fixture.Store.GetPath(first.Url.Split('/').Last())!);
        Assert.Equal(1600, image.Width);
        Assert.Equal(800, image.Height);
        Assert.Equal("Webp", image.Metadata.DecodedImageFormat!.Name);
    }

    [Fact]
    public async Task DeletingPhotoChecksItsModelAndRemovesOnlyItsOwnFile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Service.UploadAsync(1, await PhotoAsync());
        var second = await fixture.Service.UploadAsync(1, await PhotoAsync());
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Service.DeleteAsync(2, first.Id));
        Assert.Equal(2, Directory.GetFiles(fixture.Directory).Length);
        await fixture.Service.DeleteAsync(1, first.Id);
        var model = await new EquipmentService(fixture.Db).GetModelByIdAsync(1);
        Assert.Equal(second.Id, Assert.Single(model.Photos).Id);
        Assert.Single(Directory.GetFiles(fixture.Directory));
    }

    [Fact]
    public async Task MissingModelAndInvalidImageLeaveNoPhotoRecordsOrFiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<GraphQLException>(async () => await fixture.Service.UploadAsync(999, await PhotoAsync()));
        await Assert.ThrowsAsync<GraphQLException>(() => fixture.Service.UploadAsync(1, "not an image"));
        Assert.Empty(await fixture.Db.EqPhotos.ToListAsync());
        Assert.Empty(Directory.GetFiles(fixture.Directory));
        Assert.Null(fixture.Store.GetPath("../../secret.webp"));
    }

    [Fact]
    public async Task DeletingModelAlsoRemovesItsStoredPhotos()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.UploadAsync(1, await PhotoAsync());
        await new EquipmentService(fixture.Db, fixture.Store).DeleteModelAsync(1);
        Assert.Empty(await fixture.Db.EqPhotos.ToListAsync());
        Assert.Empty(Directory.GetFiles(fixture.Directory));
        await Assert.ThrowsAsync<GraphQLException>(() => new EquipmentService(fixture.Db).GetModelByIdAsync(1));
    }

    private static async Task<string> PhotoAsync()
    {
        using var image = new Image<Rgba32>(2000, 1000, new Rgba32(120, 160, 200));
        using var output = new MemoryStream();
        await image.SaveAsPngAsync(output);
        return Convert.ToBase64String(output.ToArray());
    }

    private sealed class Fixture(SqliteConnection connection, AppDbContext db, string directory, EquipmentImageStore store) : IAsyncDisposable
    {
        public AppDbContext Db => db;
        public string Directory => directory;
        public EquipmentImageStore Store => store;
        public EquipmentPhotoService Service => new(db, store);
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "EqModels" ("Id" INTEGER PRIMARY KEY, "Name" TEXT NOT NULL,
                    "Description" TEXT NOT NULL, "Category" INTEGER NOT NULL, "Access" INTEGER NOT NULL, "AttributesJson" TEXT NOT NULL);
                CREATE TABLE "EqPhotos" ("Id" INTEGER PRIMARY KEY, "EqModelId" INTEGER NOT NULL REFERENCES "EqModels"("Id") ON DELETE CASCADE,
                    "Url" TEXT NOT NULL, "Order" INTEGER NOT NULL);
                CREATE TABLE "EqItems" ("Id" INTEGER PRIMARY KEY, "EqModelId" INTEGER NOT NULL);
                CREATE TABLE "BookingItems" ("Id" INTEGER PRIMARY KEY, "EqItemId" INTEGER NOT NULL);
                INSERT INTO "EqModels" VALUES (1, 'Camera', '', 0, 0, '{{}}'), (2, 'Lens', '', 1, 0, '{{}}');
                """);
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gutv-equipment-photos-" + Guid.NewGuid().ToString("N"));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["EquipmentPhotoStorage:Path"] = directory }).Build();
            return new Fixture(connection, db, directory, new EquipmentImageStore(config, NullLogger<EquipmentImageStore>.Instance));
        }
        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
    }
}
