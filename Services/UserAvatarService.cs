using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.Services;

public class UserAvatarService(AppDbContext db, AvatarImageStore images)
{
    public async Task<User> UploadAsync(int userId, string imageBase64, CancellationToken cancellationToken = default)
    {
        var url = await images.SaveAsync(imageBase64, cancellationToken);
        try { return await SetAvatarAsync(userId, url, cancellationToken); }
        catch { images.Delete(url); throw; }
    }

    public Task<User> RemoveAsync(int userId, CancellationToken cancellationToken = default) =>
        SetAvatarAsync(userId, null, cancellationToken);

    private async Task<User> SetAvatarAsync(int userId, string? url, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize replacements across processes so a concurrent upload/removal cannot orphan a previous image.
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(6, {userId})", cancellationToken);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new GraphQLException("Пользователь не найден");
        await db.Entry(user).ReloadAsync(cancellationToken);
        var previous = user.AvatarUrl;
        user.AvatarUrl = url;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        images.Delete(previous);
        return user;
    }
}
