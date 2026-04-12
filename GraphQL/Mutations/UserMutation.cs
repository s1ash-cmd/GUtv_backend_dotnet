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
        var role = input.Ronin ? UserRole.Ronin : UserRole.User;

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

        var newAccessToken = authService.GenerateAccessToken(user);
        var newRefreshToken = authService.GenerateRefreshToken();

        await userService.SaveRefreshTokenAsync(user.Id, newRefreshToken);

        return new AuthPayload(user, newAccessToken, newRefreshToken);
    }

    [Authorize(Roles = ["Admin"])]
    public Task<User> SetUserRole(
        int userId,
        UserRole role,
        UserService userService) =>
        userService.SetRole(userId, role);

    [Authorize(Roles = ["Admin"])]
    public Task<User> SetUserBanned(
        int userId,
        bool banned,
        UserService userService) =>
        userService.SetBanned(userId, banned);

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

    [Authorize(Roles = ["Admin"])]
    public Task<User> LinkTelegramByCode(
        string code,
        long chatId,
        string? username,
        UserService userService) =>
        userService.LinkTelegramByCode(code, chatId, username);
}

public record RegisterInput(
    string Login,
    string Password,
    string Name,
    bool Ronin,
    int JoinYear
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
