using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Re-checks the signed-in manager's access on open Blazor circuits every
/// ManagerSignInService.RecheckInterval. A page left open never makes a new HTTP request, so the
/// cookie check alone wouldn't catch it. When access is gone the circuit becomes signed-out, the
/// router shows RedirectToLogin, and that page load's cookie check ends the cookie too (the uid is
/// remembered as revoked, so it doesn't wait for the cookie's own interval).
/// </summary>
public class ManagerRevalidatingAuthStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly ManagerSignInService _signIn;

    public ManagerRevalidatingAuthStateProvider(ILoggerFactory loggerFactory, ManagerSignInService signIn)
        : base(loggerFactory)
    {
        _signIn = signIn;
    }

    protected override TimeSpan RevalidationInterval => ManagerSignInService.RecheckInterval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        ClaimsPrincipal user = authenticationState.User;
        string? uid = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        ManagerSignInService.AccessCheck access = await _signIn.CheckAccessAsync(
            uid, user.FindFirst(ClaimTypes.Email)?.Value, user.FindFirst(ClaimTypes.Role)?.Value);
        // Unknown (Firebase unreachable) keeps the session; it's checked again next interval.
        return access != ManagerSignInService.AccessCheck.Revoked;
    }
}
