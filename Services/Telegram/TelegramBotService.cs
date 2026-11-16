using System.Net.Http;
using System.Net.Sockets;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
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

    // Pause before the next getUpdates after a polling error, growing while errors keep coming.
    private static readonly TimeSpan[] PollingRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    // A successful poll does not reach our code, so a quiet gap this long counts as recovery.
    // It must exceed the longest delay plus a timed-out request (HttpClient default is 100 s).
    private static readonly TimeSpan PollingFailureResetWindow = TimeSpan.FromMinutes(5);

    private int consecutivePollingFailures;
    private DateTimeOffset lastPollingFailureAt = DateTimeOffset.MinValue;

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

    private async Task HandlePollingErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - lastPollingFailureAt > PollingFailureResetWindow)
        {
            consecutivePollingFailures = 0;
        }

        lastPollingFailureAt = now;
        var delay = PollingRetryDelays[Math.Min(consecutivePollingFailures, PollingRetryDelays.Length - 1)];
        consecutivePollingFailures++;

        if (IsTransientNetworkError(exception))
        {
            logger.LogWarning(
                "Telegram polling failed ({FailureCount} in a row): {Error}. Retrying in {DelaySeconds} s",
                consecutivePollingFailures,
                DescribeChain(exception),
                delay.TotalSeconds);
        }
        else
        {
            logger.LogError(
                exception,
                "Telegram polling failed ({FailureCount} in a row). Retrying in {DelaySeconds} s",
                consecutivePollingFailures,
                delay.TotalSeconds);
        }

        await Task.Delay(delay, cancellationToken);
    }

    // DNS, TLS, connection resets and timeouts: expected on a flaky link, a stack trace adds nothing.
    private static bool IsTransientNetworkError(Exception exception)
    {
        if (exception is ApiRequestException apiError)
        {
            return apiError.ErrorCode is 429 or >= 500;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or SocketException or IOException or TimeoutException or TaskCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeChain(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!messages.Contains(current.Message))
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(" -> ", messages);
    }
}
