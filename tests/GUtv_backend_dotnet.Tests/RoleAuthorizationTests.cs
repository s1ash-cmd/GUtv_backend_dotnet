using System.Security.Claims;
using GUtv_backend_dotnet.Models;
using GUtv_backend_dotnet.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace GUtv_backend_dotnet.Tests;

public class RoleAuthorizationTests
{
    private static readonly AuthorizationPolicy AdminPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser().RequireRole("Admin").Build();
    private static readonly AuthorizationPolicy AuthenticatedPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser().Build();

    [Fact]
    public async Task DowngradedAdministratorLosesAdminPolicyWithoutLosingAuthenticatedAccess()
    {
        using var services = CreateServices();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var principal = CreateTokenPrincipal(UserRole.Admin);
        Assert.True((await authorization.AuthorizeAsync(principal, null, AdminPolicy)).Succeeded);

        CurrentUserClaims.SetRole(principal, UserRole.User);

        Assert.False((await authorization.AuthorizeAsync(principal, null, AdminPolicy)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(principal, null, AuthenticatedPolicy)).Succeeded);
        Assert.False(principal.IsInRole("Admin"));
        Assert.True(principal.IsInRole("User"));
    }

    [Fact]
    public async Task PromotionUsesCurrentRoleInsteadOfOldTokenRole()
    {
        using var services = CreateServices();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var principal = CreateTokenPrincipal(UserRole.User);
        Assert.False((await authorization.AuthorizeAsync(principal, null, AdminPolicy)).Succeeded);

        CurrentUserClaims.SetRole(principal, UserRole.Admin);

        Assert.True((await authorization.AuthorizeAsync(principal, null, AdminPolicy)).Succeeded);
        Assert.False(principal.IsInRole("User"));
    }

    [Fact]
    public void RoleSynchronizationPreservesIdentityAndProfileClaims()
    {
        var principal = CreateTokenPrincipal(UserRole.Admin);

        CurrentUserClaims.SetRole(principal, UserRole.Osnova);

        Assert.Equal("42", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("Test user", principal.Identity!.Name);
        Assert.Equal("test-seed", principal.FindFirstValue("avatarSeed"));
        Assert.Equal("Bearer", principal.Identity.AuthenticationType);
        Assert.True(principal.Identity.IsAuthenticated);
    }

    [Fact]
    public void RepeatedSynchronizationDoesNotAccumulateRoles()
    {
        var principal = CreateTokenPrincipal(UserRole.Admin);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClaimTypes.Role, "Admin"));

        CurrentUserClaims.SetRole(principal, UserRole.User);
        CurrentUserClaims.SetRole(principal, UserRole.User);

        var role = Assert.Single(principal.FindAll(ClaimTypes.Role));
        Assert.Equal("User", role.Value);
    }

    [Fact]
    public async Task StaleAdminRolesInOtherIdentitiesCannotBypassAuthorization()
    {
        using var services = CreateServices();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var principal = CreateTokenPrincipal(UserRole.Admin);
        principal.AddIdentity(new ClaimsIdentity(
            [new Claim("custom-role", "Admin")], "Secondary", ClaimTypes.Name, "custom-role"));
        principal.AddIdentity(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")]));

        CurrentUserClaims.SetRole(principal, UserRole.Osnova);

        Assert.False(principal.IsInRole("Admin"));
        Assert.True(principal.IsInRole("Osnova"));
        Assert.False((await authorization.AuthorizeAsync(principal, null, AdminPolicy)).Succeeded);
        Assert.Single(principal.Identities.SelectMany(identity => identity.FindAll(identity.RoleClaimType)));
    }

    [Fact]
    public void PrimaryIdentityCustomRoleClaimTypeIsRespected()
    {
        var identity = new ClaimsIdentity(
            [new Claim("permission-role", "Admin")], "Bearer", ClaimTypes.Name, "permission-role");
        var principal = new ClaimsPrincipal(identity);

        CurrentUserClaims.SetRole(principal, UserRole.User);

        Assert.False(principal.IsInRole("Admin"));
        Assert.True(principal.IsInRole("User"));
        Assert.Equal("permission-role", Assert.Single(identity.FindAll(identity.RoleClaimType)).Type);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal CreateTokenPrincipal(UserRole role) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim(ClaimTypes.Name, "Test user"),
            new Claim("avatarSeed", "test-seed"),
            new Claim(ClaimTypes.Role, role.ToString())
        ], "Bearer", ClaimTypes.Name, ClaimTypes.Role));
}
