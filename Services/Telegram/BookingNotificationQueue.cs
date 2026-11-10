using System.Threading.Channels;

namespace GUtv_backend_dotnet.Services.Telegram;

// Sends booking notifications after the request that caused them has been answered.
// The queue lives in memory: notifications still waiting when the process stops are lost,
// the bookings themselves are already saved.
public sealed class BookingNotificationQueue(
    IServiceScopeFactory scopeFactory,
    ILogger<BookingNotificationQueue> logger) : BackgroundService
{
    private const int Capacity = 500;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private readonly Channel<Func<TelegramNotificationService, CancellationToken, Task>> jobs =
        Channel.CreateBounded<Func<TelegramNotificationService, CancellationToken, Task>>(
            new BoundedChannelOptions(Capacity)
            {
                // When Telegram has been unreachable for a long time, the newest news matters most.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

    public void Enqueue(Func<TelegramNotificationService, CancellationToken, Task> send) =>
        jobs.Writer.TryWrite(send);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var send in jobs.Reader.ReadAllAsync(stoppingToken))
                await SendAsync(send, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SendAsync(
        Func<TelegramNotificationService, CancellationToken, Task> send, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(SendTimeout);
        try
        {
            // The request's services are gone by now; each job gets its own database context.
            await using var scope = scopeFactory.CreateAsyncScope();
            var notifications = scope.ServiceProvider.GetRequiredService<TelegramNotificationService>();
            await send(notifications, timeout.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // One failed notification must not stop the ones queued behind it.
            logger.LogError(exception, "Booking notification was not delivered");
        }
    }
}
