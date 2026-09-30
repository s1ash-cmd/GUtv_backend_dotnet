using System.Security.Claims;
using GUtv_backend_dotnet.Models;

namespace GUtv_backend_dotnet.Services;

public static class CurrentUserClaims
{
    public static void SetRole(ClaimsPrincipal principal, UserRole role)
    {
        var authenticatedIdentity = principal.Identities.First(identity => identity.IsAuthenticated);

        // IsInRole checks every identity, so no stale role claim may remain.
        foreach (var identity in principal.Identities)
        {
            foreach (var claim in identity.FindAll(identity.RoleClaimType).ToArray())
                identity.RemoveClaim(claim);
        }

        authenticatedIdentity.AddClaim(new Claim(authenticatedIdentity.RoleClaimType, role.ToString()));
    }
}
