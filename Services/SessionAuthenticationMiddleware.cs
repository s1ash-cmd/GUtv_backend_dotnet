using System.Security.Claims;
using GUtv_backend_dotnet.Data;
using Microsoft.EntityFrameworkCore;

namespace GUtv_backend_dotnet.Services;

public class SessionAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (!int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ||
                !Guid.TryParse(context.User.FindFirstValue("sid"), out var sessionId))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var now = DateTime.UtcNow;
            var currentUser = await db.UserSessions.AsNoTracking()
                .Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null &&
                    s.ExpiresAt > now && !s.User.Banned)
                .Select(s => new { s.User.Role })
                .SingleOrDefaultAsync(context.RequestAborted);
            if (currentUser is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            CurrentUserClaims.SetRole(context.User, currentUser.Role);
        }
        await next(context);
    }
}
