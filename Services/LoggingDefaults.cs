namespace GUtv_backend_dotnet.Services;

public static class LoggingDefaults
{
    // EF Core logs every SQL statement it runs at Information under this category: hundreds of
    // thousands of journal lines a day on the server. Failed commands are logged as errors and
    // stay visible.
    public const string SqlCommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";

    // Applied only when the configuration does not set the category itself, so the statements
    // can still be turned on for debugging, e.g. Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Information.
    // This lives in code rather than appsettings.json, which the server's checkout keeps modified locally.
    public static void Apply(ILoggingBuilder logging, IConfiguration configuration)
    {
        if (configuration[$"Logging:LogLevel:{SqlCommandCategory}"] is null)
            logging.AddFilter(SqlCommandCategory, LogLevel.Warning);
    }
}
