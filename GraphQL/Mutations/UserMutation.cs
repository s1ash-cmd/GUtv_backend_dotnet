using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;

namespace GUtv_backend_dotnet.GraphQL.Mutations;

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