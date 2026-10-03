using System.Security.Cryptography;
using GUtv_backend_dotnet.Data;
using GUtv_backend_dotnet.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
        if (string.IsNullOrWhiteSpace(login))
            throw new GraphQLException("Логин обязателен");

        if (string.IsNullOrWhiteSpace(name))
            throw new GraphQLException("Имя обязательно");

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new GraphQLException("Пароль обязателен и должен быть не короче 8 символов");

        login = login.Trim();
        name = name.Trim();

        // Hash outside the lock so password hashing does not serialize registrations.
        var passwordHash = HashPassword(password);
        await using var transaction = await _db.Database.BeginTransactionAsync();
        // A single database lock also makes first-administrator selection atomic.
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(4, 0)");

        if (await FindByLogin(login).AnyAsync())
            throw new GraphQLException("Пользователь с таким логином уже существует");

        var user = new User
        {
            Login = login,
            PasswordHash = passwordHash,
            Name = name,
            AvatarSeed = GenerateAvatarSeed(),
            Role = role,
            JoinYear = joinYear ?? DateTime.UtcNow.Year
        };

        if (!await _db.Users.AnyAsync())
            user.Role = UserRole.Admin;

        _db.Users.Add(user);
        try
        {
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
               { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Users_NormalizedLogin" })
        {
            throw new GraphQLException("Пользователь с таким логином уже существует");
        }
        return user;
    }

    public async Task<User?> GetByLoginAsync(string login)
    {
        return await FindByLogin(login.Trim()).FirstOrDefaultAsync();
    }

    // Normalize both operands in PostgreSQL, using the same rules as the unique column.
    // Parameters stay SQL parameters: '%' and '_' are literal login characters.
    private IQueryable<User> FindByLogin(string login) =>
        _db.Users.FromSqlInterpolated($"SELECT * FROM \"Users\" WHERE \"NormalizedLogin\" = lower(btrim({login}))");

    public async Task<User?> GetByIdAsync(int id)
    {
        return await _db.Users.FindAsync(id);
    }

    public async Task<User> RegenerateAvatarSeedAsync(int userId)
    {
        var user = await _db.Users.FindAsync(userId)
            ?? throw new GraphQLException("Пользователь не найден");

        user.AvatarSeed = GenerateAvatarSeed();
        await _db.SaveChangesAsync();
        return user;
    }

    public bool VerifyPassword(string password, string passwordHash)
    {
        return BCrypt.Net.BCrypt.Verify(password, passwordHash);
    }

    public async Task<User?> GetByTelegramChatIdAsync(long chatId)
    {
        return await _db.Users.FirstOrDefaultAsync(u => u.TelegramChatId == chatId);
    }

    public async Task<User> EnsureRoleUpgradeOnAuthorizationAsync(User user)
    {
        if (user.Role != UserRole.User)
            return user;

        if (user.JoinYear > DateTime.UtcNow.Year - 1)
            return user;

        user.Role = UserRole.Osnova;
        await _db.SaveChangesAsync();
        return user;
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
        await using var transaction = await _db.Database.BeginTransactionAsync();
        var user = await _db.Users.FindAsync(userId)
            ?? throw new GraphQLException("Пользователь не найден");

        user.Banned = banned;
        await _db.SaveChangesAsync();
        if (banned)
        {
            var now = DateTime.UtcNow;
            await _db.UserSessions.Where(s => s.UserId == userId && s.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.RevokedAt, now));
        }
        await transaction.CommitAsync();
        return user;
    }

    public async Task<string> GenerateTelegramLinkCode(int userId)
    {
        var user = await _db.Users.FindAsync(userId)
            ?? throw new GraphQLException("Пользователь не найден");

        if (user.TelegramChatId.HasValue)
            throw new GraphQLException("Telegram уже привязан");

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        user.TelegramLinkCode = code;
        user.TelegramLinkCodeExpiry = DateTime.UtcNow.AddMinutes(10);

        await _db.SaveChangesAsync();
        return code;
    }

    public async Task<User> LinkTelegramByCode(string code, long chatId, string? username)
    {
        if (chatId <= 0)
            throw new GraphQLException("Привязать Telegram можно только в личном чате с ботом");

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

    public async Task UpdateTelegramUsernameAsync(long chatId, string? newUsername)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.TelegramChatId == chatId);

        if (user != null && user.TelegramUsername != newUsername)
        {
            user.TelegramUsername = newUsername;
            await _db.SaveChangesAsync();
        }
    }

    public string GenerateTelegramDeepLink(string code, string botUsername)
    {
        botUsername = botUsername.TrimStart('@');
        return $"https://t.me/{botUsername}?start=LINK_{code}";
    }

    private static string HashPassword(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password);
    }

    private static string GenerateAvatarSeed()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    }
}
