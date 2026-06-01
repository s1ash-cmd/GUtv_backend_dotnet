using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace GUtv_backend_dotnet.Services.Telegram;

public class TelegramBotService(
    ILogger<TelegramBotService> logger,
    IServiceProvider serviceProvider,
    ITelegramBotClient botClient) : BackgroundService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>()
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var me = await botClient.GetMe(stoppingToken);
                logger.LogInformation("Telegram bot @{BotUsername} started", me.Username);

                await botClient.ReceiveAsync(
                    HandleUpdateAsync,
                    HandlePollingErrorAsync,
                    receiverOptions,
                    stoppingToken);

                if (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "Telegram bot receiver stopped. Restarting in {DelaySeconds} seconds",
                        RestartDelay.TotalSeconds);
                    await Task.Delay(RestartDelay, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Telegram bot receiver crashed. Restarting in {DelaySeconds} seconds",
                    RestartDelay.TotalSeconds);

                try
                {
                    await Task.Delay(RestartDelay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<TelegramUpdateHandler>();
            await handler.HandleUpdateAsync(client, update, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Telegram update handling failed");
        }
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Telegram polling failed");
        return Task.CompletedTask;
    }
}
