using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.Services;

public class UserSessionService(AppDbContext db, UserService userService, AuthService authService)
{
    public static string HashRefreshToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static Guid GetRequiredSessionId(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirstValue("sid"), out var id) && id != Guid.Empty
            ? id : throw new GraphQLException("Недействительная сессия");

    public async Task<RefreshedSession> CreateAsync(int userId, string? userAgent)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var user = await LockUserAsync(userId);
            if (user.Banned) throw new GraphQLException("Пользователь заблокирован");
            user = await userService.EnsureRoleUpgradeOnAuthorizationAsync(user);
            var now = DateTime.UtcNow;
            var token = authService.GenerateRefreshToken();
            var session = new UserSession
            {
                Id = Guid.NewGuid(), UserId = userId, RefreshTokenHash = HashRefreshToken(token),
                CreatedAt = now, LastUsedAt = now, ExpiresAt = now.AddDays(7),
                UserAgent = NormalizeUserAgent(userAgent)
            };
            var accessToken = authService.GenerateAccessToken(user, session.Id);
            db.UserSessions.Add(session);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new RefreshedSession(user, accessToken, token);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<RefreshedSession> RotateAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 256)
            throw new GraphQLException("Недействительный refresh token");
        var hash = HashRefreshToken(refreshToken);
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var session = await db.UserSessions.AsNoTracking().SingleOrDefaultAsync(s => s.RefreshTokenHash == hash)
                ?? throw new GraphQLException("Недействительный refresh token");
            // Lock the account before its sessions, matching logout-all and banning.
            // This also serializes role upgrades with administrative role changes.
            var user = await LockUserAsync(session.UserId);
            if (user.Banned) throw new GraphQLException("Пользователь заблокирован");
            var now = DateTime.UtcNow;
            var token = authService.GenerateRefreshToken();
            var updated = await db.UserSessions
                .Where(s => s.Id == session.Id && s.RefreshTokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.RefreshTokenHash, HashRefreshToken(token))
                    .SetProperty(s => s.LastUsedAt, now)
                    .SetProperty(s => s.ExpiresAt, now.AddDays(7)));
            if (updated != 1) throw new GraphQLException("Недействительный refresh token");
            user = await userService.EnsureRoleUpgradeOnAuthorizationAsync(user);
            var accessToken = authService.GenerateAccessToken(user, session.Id);
            await transaction.CommitAsync();
            return new RefreshedSession(user, accessToken, token);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public Task<List<UserSessionPayload>> GetActiveAsync(int userId, Guid currentSessionId)
    {
        var now = DateTime.UtcNow;
        return db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastUsedAt).ThenBy(s => s.Id)
            .Select(s => new UserSessionPayload(s.Id, s.CreatedAt, s.LastUsedAt, s.ExpiresAt,
                s.UserAgent, s.Id == currentSessionId)).ToListAsync();
    }

    public async Task<bool> RevokeAsync(int userId, Guid sessionId)
    {
        var now = DateTime.UtcNow;
        return await db.UserSessions.Where(s => s.UserId == userId && s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.RevokedAt, now)) == 1;
    }

    public async Task<bool> RevokeAllAsync(int userId)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await LockUserAsync(userId);
        var now = DateTime.UtcNow;
        await db.UserSessions.Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.RevokedAt, now));
        await transaction.CommitAsync();
        return true;
    }

    private async Task<User> LockUserAsync(int userId)
    {
        // SQLite regression tests serialize writes in their database transaction.
        var query = db.Database.IsNpgsql()
            ? db.Users.FromSqlInterpolated($"SELECT * FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE")
            : db.Users.Where(u => u.Id == userId);
        var user = await query.SingleOrDefaultAsync()
            ?? throw new GraphQLException("Пользователь не найден");
        await db.Entry(user).ReloadAsync();
        return user;
    }

    private static string? NormalizeUserAgent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sanitized = new string(value.Where(c => !char.IsControl(c)).Take(500).ToArray()).Trim();
        return sanitized.Length == 0 ? null : sanitized;
    }
}

public record UserSessionPayload(Guid Id, DateTime CreatedAt, DateTime LastUsedAt,
    DateTime ExpiresAt, string? UserAgent, bool IsCurrent);

public record RefreshedSession(User User, string AccessToken, string RefreshToken);
