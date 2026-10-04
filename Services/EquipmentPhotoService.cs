using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.Services;

public sealed class EquipmentPhotoService(AppDbContext db, EquipmentImageStore images)
{
    public async Task<EqPhoto> UploadAsync(int modelId, string imageBase64, CancellationToken cancellationToken = default)
    {
        var url = await images.SaveAsync(imageBase64, cancellationToken);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // The model row also serializes photo writes with model deletion.
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"EqModels\" WHERE \"Id\" = {modelId} FOR UPDATE", cancellationToken);
            if (!await db.EqModels.AnyAsync(m => m.Id == modelId, cancellationToken))
                throw new GraphQLException("Модель оборудования не найдена");
            var lastOrder = await db.EqPhotos.Where(p => p.EqModelId == modelId)
                .MaxAsync(p => (int?)p.Order, cancellationToken) ?? -1;
            var photo = new EqPhoto { EqModelId = modelId, Url = url, Order = lastOrder + 1 };
            db.EqPhotos.Add(photo);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return photo;
        }
        catch { images.Delete(url); throw; }
    }

    public async Task<bool> DeleteAsync(int modelId, int photoId, CancellationToken cancellationToken = default)
    {
        var photo = await db.EqPhotos.FirstOrDefaultAsync(p => p.Id == photoId && p.EqModelId == modelId, cancellationToken)
            ?? throw new GraphQLException("Фото оборудования не найдено");
        db.EqPhotos.Remove(photo);
        await db.SaveChangesAsync(cancellationToken);
        images.Delete(photo.Url);
        return true;
    }
}
