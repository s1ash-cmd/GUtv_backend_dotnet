namespace GUtv_backend_dotnet.Services;

// The refresh token lives in an HttpOnly cookie, so a script injected into the site cannot
// read it. Only the short-lived access token is handed to the page.
public static class RefreshTokenCookie
{
    public const string Name = "gutv_refresh";
    // Only the GraphQL endpoint reads the cookie. In production the site and the API share
    // https://gutvbooker.ru, and nginx sends every other path to the Next.js server, which
    // has no use for the refresh token.
    public const string Path = "/graphql";

    public static string? Read(HttpContext? context)
    {
        var value = context?.Request.Cookies[Name];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static void Set(HttpContext? context, string refreshToken, DateTime expiresAtUtc)
    {
        if (context is null) return;
        var options = CreateOptions(context);
        options.Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc));
        context.Response.Cookies.Append(Name, refreshToken, options);
    }

    public static void Clear(HttpContext? context)
    {
        if (context is null) return;
        // A cookie is only removed when the path and attributes match the ones it was set with.
        context.Response.Cookies.Delete(Name, CreateOptions(context));
    }

    private static CookieOptions CreateOptions(HttpContext context)
    {
        var configuration = context.RequestServices?.GetService<IConfiguration>();
        // Lax suits the production setup, where the site and the API are one origin, and
        // also a site and an API on the same registrable domain. An API on an unrelated
        // domain would need "None", which browsers accept only over HTTPS.
        var sameSite = Enum.TryParse<SameSiteMode>(configuration?["Auth:RefreshCookieSameSite"], true, out var mode)
            ? mode
            : SameSiteMode.Lax;

        return new CookieOptions
        {
            HttpOnly = true,
            // Local development runs over plain HTTP; behind nginx the forwarded scheme is HTTPS.
            Secure = context.Request.IsHttps,
            SameSite = sameSite,
            Path = Path,
            IsEssential = true
        };
    }
}
