using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// The cookie's OnValidatePrincipal: on page requests, re-checks the signed-in manager's access
/// every ManagerSignInService.RecheckInterval, and signs the session out once it's revoked.
/// Requests in between cost nothing but a claim read.
/// </summary>
public static class ManagerSessionValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        ClaimsPrincipal? principal = context.Principal;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var signIn = context.HttpContext.RequestServices.GetRequiredService<ManagerSignInService>();
        string? uid = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        bool revoked = uid is null || signIn.IsKnownRevoked(uid);
        if (!revoked && ManagerSignInService.NeedsRecheck(principal))
        {
            ManagerSignInService.AccessCheck access = await signIn.CheckAccessAsync(
                uid, principal.FindFirst(ClaimTypes.Email)?.Value, principal.FindFirst(ClaimTypes.Role)?.Value);
            revoked = access == ManagerSignInService.AccessCheck.Revoked;

            if (access == ManagerSignInService.AccessCheck.Allowed)
            {
                context.ReplacePrincipal(ManagerSignInService.WithFreshValidation(principal));
                context.ShouldRenew = true;
            }
            // Unknown: keep the session; the next request tries again.
        }

        if (revoked)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
