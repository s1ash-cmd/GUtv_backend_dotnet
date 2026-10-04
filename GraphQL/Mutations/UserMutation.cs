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
        UserSessionService sessionService,
        IHttpContextAccessor httpContextAccessor)
    {
        var role = UserRole.User;

        var user = await userService.CreateUser(
            input.Login,
            input.Password,
            input.Name,
            role,
            joinYear: null);

        var session = await sessionService.CreateAsync(user.Id, httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString());
        return new AuthPayload(session.User, session.AccessToken, session.RefreshToken);
    }

    public async Task<AuthPayload> Login(
        LoginInput input,
        UserService userService,
        UserSessionService sessionService,
        IHttpContextAccessor httpContextAccessor)
    {
        var user = await userService.GetByLoginAsync(input.Login);

        if (user == null || !userService.VerifyPassword(input.Password, user.PasswordHash))
            throw new GraphQLException("Неверный логин или пароль");

        if (user.Banned)
            throw new GraphQLException("Пользователь заблокирован");

        var session = await sessionService.CreateAsync(user.Id, httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString());
        return new AuthPayload(session.User, session.AccessToken, session.RefreshToken);
    }

    public async Task<AuthPayload> RefreshToken(
        string refreshToken,
        UserSessionService sessionService)
    {
        var session = await sessionService.RotateAsync(refreshToken);
        return new AuthPayload(session.User, session.AccessToken, session.RefreshToken);
    }

    [Authorize]
    public Task<bool> Logout(IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService, UserSessionService sessionService)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        var userId = equipmentService.GetRequiredUserId(principal);
        return sessionService.RevokeAsync(userId, UserSessionService.GetRequiredSessionId(principal));
    }

    [Authorize]
    public Task<bool> RevokeMySession(Guid sessionId, IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService, UserSessionService sessionService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return sessionService.RevokeAsync(userId, sessionId);
    }

    [Authorize]
    public Task<bool> LogoutAll(IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService, UserSessionService sessionService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return sessionService.RevokeAllAsync(userId);
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

    [Authorize]
    public Task<User> UploadMyAvatar(
        string imageBase64,
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        UserAvatarService avatarService,
        CancellationToken cancellationToken)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return avatarService.UploadAsync(userId, imageBase64, cancellationToken);
    }

    [Authorize]
    public Task<User> RemoveMyAvatar(
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        UserAvatarService avatarService,
        CancellationToken cancellationToken)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return avatarService.RemoveAsync(userId, cancellationToken);
    }

    [Authorize]
    public async Task<User> RegenerateMyAvatar(
        IHttpContextAccessor httpContextAccessor,
        EquipmentService equipmentService,
        UserService userService)
    {
        var userId = equipmentService.GetRequiredUserId(httpContextAccessor.HttpContext?.User);
        return await userService.RegenerateAvatarSeedAsync(userId);
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
