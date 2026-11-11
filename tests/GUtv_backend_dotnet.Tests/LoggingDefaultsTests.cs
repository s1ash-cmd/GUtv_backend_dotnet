using GUtv_backend_dotnet.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GUtv_backend_dotnet.Tests;

public class LoggingDefaultsTests
{
    [Fact]
    public void SqlStatementsAreNotLoggedButFailedCommandsAndOtherCategoriesAre()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Information"
        });

        var sql = factory.CreateLogger(LoggingDefaults.SqlCommandCategory);
        Assert.False(sql.IsEnabled(LogLevel.Information));
        Assert.True(sql.IsEnabled(LogLevel.Warning));
        Assert.True(sql.IsEnabled(LogLevel.Error));
        // Neighbouring EF categories (migrations, query warnings) keep the configured level.
        Assert.True(factory.CreateLogger("Microsoft.EntityFrameworkCore.Migrations").IsEnabled(LogLevel.Information));
        Assert.True(factory.CreateLogger("GUtv_backend_dotnet.Services.BookingService").IsEnabled(LogLevel.Information));
    }

    [Fact]
    public void ConfigurationCanStillTurnTheStatementsOnForDebugging()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Information",
            [$"Logging:LogLevel:{LoggingDefaults.SqlCommandCategory}"] = "Information"
        });

        Assert.True(factory.CreateLogger(LoggingDefaults.SqlCommandCategory).IsEnabled(LogLevel.Information));
    }

    private static ILoggerFactory CreateFactory(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            // The same order as WebApplication.CreateBuilder: configuration first, then the default.
            logging.AddConfiguration(configuration.GetSection("Logging"));
            LoggingDefaults.Apply(logging, configuration);
            logging.AddProvider(new NullProvider());
        });
        return services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
    }

    // Accepts everything: whether a level is enabled is decided by the factory's filter rules.
    private sealed class NullProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new EnabledLogger();
        public void Dispose() { }
    }

    private sealed class EnabledLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) { }
    }
}
