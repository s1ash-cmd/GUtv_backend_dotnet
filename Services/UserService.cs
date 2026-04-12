using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.Services;

public class UserService
{
    private readonly AppDbContext _db;

    public UserService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<User> CreateUser(
        string login, string password, string name,
        UserRole role = UserRole.User, int? joinYear = null)
    {
        if (await _db.Users.AnyAsync(u => EF.Functions.ILike(u.Login, login)))
            throw new GraphQLException("Пользователь с таким логином уже существует");

        var user = new User
        {
            Login = login,
            PasswordHash = HashPassword(password),
            Name = name,
            Role = role,
            JoinYear = joinYear ?? DateTime.UtcNow.Year
        };

        if (!await _db.Users.AnyAsync())
            user.Role = UserRole.Admin;

        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task<User?> GetByLoginAsync(string login)
    {
        return await _db.Users
            .FirstOrDefaultAsync(u => EF.Functions.ILike(u.Login, login));
    }

    public bool VerifyPassword(string password, string passwordHash)
    {
        return BCrypt.Net.BCrypt.Verify(password, passwordHash);
    }

    public async Task SaveRefreshTokenAsync(int userId, string refreshToken)
    {
        var user = await _db.Users.FindAsync(userId)
            ?? throw new GraphQLException("Пользователь не найден");

        user.RefreshToken = refreshToken;
        user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7);
        await _db.SaveChangesAsync();
    }

    public async Task<User?> GetByRefreshTokenAsync(string refreshToken)
    {
        return await _db.Users.FirstOrDefaultAsync(u =>
            u.RefreshToken == refreshToken &&
            u.RefreshTokenExpiryTime > DateTime.UtcNow);
    }

    public async Task<User> SetRole(int userId, UserRole role)
    {
        var user = await _db.Users.FindAsync(userId)
            ?? throw new GraphQLException("Пользователь не найден");

        user.Role = role;
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task<User> SetBanned(int userId, bool banned)
    {
        var user = await _db.Users.FindAsync(userId)
            ?? throw new GraphQLException("Пользователь не найден");

        user.Banned = banned;
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task<string> GenerateTelegramLinkCode(int userId)
    {
        var user = await _db.Users.FindAsync(userId)
            ?? throw new GraphQLException("Пользователь не найден");

        if (user.TelegramChatId.HasValue)
            throw new GraphQLException("Telegram уже привязан");

        var code = Random.Shared.Next(100000, 999999).ToString();
        user.TelegramLinkCode = code;
        user.TelegramLinkCodeExpiry = DateTime.UtcNow.AddMinutes(10);

        await _db.SaveChangesAsync();
        return code;
    }

    public async Task<User> LinkTelegramByCode(string code, long chatId, string? username)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.TelegramLinkCode == code)
            ?? throw new GraphQLException("Неверный код привязки");

        if (user.TelegramLinkCodeExpiry < DateTime.UtcNow)
            throw new GraphQLException("Срок действия кода истёк");

        var existing = await _db.Users.FirstOrDefaultAsync(u => u.TelegramChatId == chatId);
        if (existing != null)
            throw new GraphQLException(existing.Id == user.Id
                ? "Telegram уже привязан к вашему аккаунту"
                : "Telegram привязан к другому аккаунту");

        user.TelegramChatId = chatId;
        user.TelegramUsername = username;
        user.TelegramLinkCode = null;
        user.TelegramLinkCodeExpiry = null;

        await _db.SaveChangesAsync();
        return user;
    }

    public async Task<bool> UnlinkTelegram(int userId)
    {
        var user = await _db.Users.FindAsync(userId)
            ?? throw new GraphQLException("Пользователь не найден");

        user.TelegramChatId = null;
        user.TelegramUsername = null;
        user.TelegramLinkCode = null;
        user.TelegramLinkCodeExpiry = null;

        await _db.SaveChangesAsync();
        return true;
    }

    private static string HashPassword(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password);
    }
}