using HotChocolate;

namespace GUtv_backend_dotnet.Services;

public class BotSecurityService(IConfiguration configuration)
{
    private readonly string _botToken = configuration["BotConfiguration:BotToken"]
        ?? throw new InvalidOperationException("Bot Token is not configured");

    public void EnsureAuthorized(string botToken)
    {
        if (string.IsNullOrWhiteSpace(botToken) || botToken != _botToken)
            throw new GraphQLException("У вас нет прав для этого действия");
    }
}
