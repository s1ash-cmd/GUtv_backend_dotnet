using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using HotChocolate.Authorization;
using Microsoft.AspNetCore.Http;

namespace GUtv_backend_dotnet.GraphQL.Mutations;

[ExtendObjectType(typeof(Mutation))]
public class UserMutation
{
    public async Task<AuthPayload> Register(
        RegisterInput input,
        UserService userService,
        AuthService authService)
    {
        var role = UserRole.User;

        var user = await userService.CreateUser(
            input.Login,
            input.Password,
            input.Name,
            role,
            input.JoinYear);

        var accessToken = authService.GenerateAccessToken(user);
        var refreshToken = authService.GenerateRefreshToken();

        await userService.SaveRefreshTokenAsync(user.Id, refreshToken);

        return new AuthPayload(user, accessToken, refreshToken);
    }

    public async Task<AuthPayload> Login(
        LoginInput input,
        UserService userService,
        AuthService authService)
    {
        var user = await userService.GetByLoginAsync(input.Login);

        if (user == null || !userService.VerifyPassword(input.Password, user.PasswordHash))
            throw new GraphQLException("Неверный логин или пароль");

        if (user.Banned)
            throw new GraphQLException("Пользователь заблокирован");

        user = await userService.EnsureRoleUpgradeOnAuthorizationAsync(user);

        var accessToken = authService.GenerateAccessToken(user);
        var refreshToken = authService.GenerateRefreshToken();

        await userService.SaveRefreshTokenAsync(user.Id, refreshToken);

        return new AuthPayload(user, accessToken, refreshToken);
    }

    public async Task<AuthPayload> RefreshToken(
        string refreshToken,
        UserService userService,
        AuthService authService)
    {
        var user = await userService.GetByRefreshTokenAsync(refreshToken);

        if (user == null)
            throw new GraphQLException("Недействительный refresh token");

        if (user.Banned)
            throw new GraphQLException("Пользователь заблокирован");

        user = await userService.EnsureRoleUpgradeOnAuthorizationAsync(user);

        var newAccessToken = authService.GenerateAccessToken(user);
        var newRefreshToken = authService.GenerateRefreshToken();

        await userService.SaveRefreshTokenAsync(user.Id, newRefreshToken);

        return new AuthPayload(user, newAccessToken, newRefreshToken);
    }

    [Authorize(Roles = ["Admin"])]
    public Task<User> SetUserRole(
        int userId,
        UserRole role,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        UserService userService)
    {
        var currentUserId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        if (currentUserId == userId)
            throw new GraphQLException("Нельзя изменять собственную роль через админскую операцию");

        return userService.SetRole(userId, role);
    }

    [Authorize(Roles = ["Admin"])]
    public Task<User> SetUserBanned(
        int userId,
        bool banned,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        UserService userService)
    {
        var currentUserId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        if (currentUserId == userId)
            throw new GraphQLException("Нельзя изменять собственный статус через админскую операцию");

        return userService.SetBanned(userId, banned);
    }

    [Authorize]
    public async Task<TelegramLinkPayload> GenerateMyTelegramLinkCode(
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        UserService userService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        var code = await userService.GenerateTelegramLinkCode(userId);
        return new TelegramLinkPayload(code);
    }

    [Authorize]
    public async Task<bool> UnlinkMyTelegram(
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        UserService userService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return await userService.UnlinkTelegram(userId);
    }

    public Task<User> LinkTelegramByCode(
        string botToken,
        string code,
        long chatId,
        string? username,
        BotSecurityService botSecurityService,
        UserService userService)
    {
        botSecurityService.EnsureAuthorized(botToken);
        return userService.LinkTelegramByCode(code, chatId, username);
    }

    public async Task<bool> UpdateTelegramUsername(
        string botToken,
        long chatId,
        string? username,
        BotSecurityService botSecurityService,
        UserService userService)
    {
        botSecurityService.EnsureAuthorized(botToken);
        await userService.UpdateTelegramUsernameAsync(chatId, username);
        return true;
    }
}

public record RegisterInput(
    string Login,
    string Password,
    string Name,
    bool IsOrganization,
    int? JoinYear
);

public record LoginInput(
    string Login,
    string Password
);

public record AuthPayload(
    User User,
    string AccessToken,
    string RefreshToken
);

public record TelegramLinkPayload(string Code);
