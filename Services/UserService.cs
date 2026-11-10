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

    public const int UsersPageSize = 50;

    // The administrators' user list: one page, plus how many people each tab holds.
    public async Task<UserPagePayload> GetUsersPageAsync(
        int page = 1, string? search = null, UserListGroup group = UserListGroup.All, bool descending = false)
    {
        if (page < 1)
            throw new GraphQLException("Номер страницы должен быть больше 0");

        var byGroup = await _db.Users.AsNoTracking()
            .GroupBy(u => new { u.Banned, u.Role })
            .Select(g => new { g.Key.Banned, g.Key.Role, Count = g.Count() })
            .ToListAsync();
        int CountOf(UserListGroup wanted) =>
            byGroup.Where(g => GroupOf(g.Banned, g.Role) == wanted).Sum(g => g.Count);
        var counts = new UserGroupCounts(
            byGroup.Sum(g => g.Count), CountOf(UserListGroup.Admins), CountOf(UserListGroup.Osnova),
            CountOf(UserListGroup.Users), CountOf(UserListGroup.Banned));

        var query = _db.Users.AsNoTracking();
        // A banned person is listed under "banned" only, whatever their role.
        query = group switch
        {
            UserListGroup.Banned => query.Where(u => u.Banned),
            UserListGroup.Admins => query.Where(u => !u.Banned && u.Role == UserRole.Admin),
            UserListGroup.Osnova => query.Where(u =>
                !u.Banned && (u.Role == UserRole.Osnova || u.Role == UserRole.Ronin)),
            UserListGroup.Users => query.Where(u => !u.Banned && u.Role == UserRole.User),
            _ => query
        };

        var term = search?.Trim().TrimStart('@').ToLowerInvariant();
        if (!string.IsNullOrEmpty(term))
            query = query.Where(u =>
                u.Name.ToLower().Contains(term) ||
                u.Login.ToLower().Contains(term) ||
                (u.TelegramUsername != null && u.TelegramUsername.ToLower().Contains(term)));

        var totalCount = await query.CountAsync();
        var lastPage = totalCount == 0 ? 1 : (totalCount - 1) / UsersPageSize + 1;
        page = Math.Min(page, lastPage);
        var ordered = descending
            ? query.OrderByDescending(u => u.Name.ToLower()).ThenByDescending(u => u.Id)
            : query.OrderBy(u => u.Name.ToLower()).ThenBy(u => u.Id);
        var items = await ordered.Skip((page - 1) * UsersPageSize).Take(UsersPageSize).ToListAsync();

        return new UserPagePayload(items, totalCount, page, UsersPageSize, counts);
    }

    private static UserListGroup GroupOf(bool banned, UserRole role) =>
        banned ? UserListGroup.Banned
        : role == UserRole.Admin ? UserListGroup.Admins
        : role is UserRole.Osnova or UserRole.Ronin ? UserListGroup.Osnova
        : UserListGroup.Users;

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

    // A login attempt for an unknown account still pays for one password check, so the
    // response time does not reveal which logins exist.
    private static readonly Lazy<string> UnknownAccountHash =
        new(() => BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")));

    public async Task<User?> FindByCredentialsAsync(string login, string password)
    {
        var user = await GetByLoginAsync(login);
        var passwordMatches = VerifyPassword(password, user?.PasswordHash ?? UnknownAccountHash.Value);
        return user is not null && passwordMatches ? user : null;
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

    public async Task<User> SetBanned(int userId, bool banned, string? reason = null)
    {
        var normalizedReason = banned && !string.IsNullOrWhiteSpace(reason) ? reason.Trim() : null;
        if (normalizedReason?.Length > 1000)
            throw new GraphQLException("Причина блокировки не должна превышать 1000 символов");

        var user = await _db.Users.FindAsync(userId)
            ?? throw new GraphQLException("Пользователь не найден");

        user.Banned = banned;
        user.BanReason = normalizedReason;
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task<string> GenerateTelegramLinkCode(int userId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        await LockTelegramLinksAsync();

        var user = await _db.Users.FindAsync(userId)
            ?? throw new GraphQLException("Пользователь не найден");

        if (user.TelegramChatId.HasValue)
            throw new GraphQLException("Telegram уже привязан");

        var now = DateTime.UtcNow;
        string code;
        do code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        while (await _db.Users.AnyAsync(u =>
                   u.Id != userId && u.TelegramLinkCode == code && u.TelegramLinkCodeExpiry > now));

        user.TelegramLinkCode = code;
        user.TelegramLinkCodeExpiry = now.AddMinutes(10);

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
        return code;
    }

    public async Task<User> LinkTelegramByCode(string code, long chatId, string? username)
    {
        if (chatId <= 0)
            throw new GraphQLException("Привязать Telegram можно только в личном чате с ботом");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        await LockTelegramLinksAsync();

        var now = DateTime.UtcNow;
        var user = await _db.Users.FirstOrDefaultAsync(u =>
                       u.TelegramLinkCode == code && u.TelegramLinkCodeExpiry > now)
            ?? throw new GraphQLException("Неверный или просроченный код привязки");

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
        await transaction.CommitAsync();
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

    // Code generation and linking share one lock, so an active code belongs to one account
    // and a Telegram chat is linked to at most one account.
    private async Task LockTelegramLinksAsync()
    {
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7, 0)");
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

public enum UserListGroup
{
    All,
    Admins,
    Osnova,
    Users,
    Banned
}

public record UserGroupCounts(int All, int Admins, int Osnova, int Users, int Banned);

public record UserPagePayload(
    IReadOnlyList<User> Items,
    int TotalCount,
    int Page,
    int PageSize,
    UserGroupCounts Counts);
