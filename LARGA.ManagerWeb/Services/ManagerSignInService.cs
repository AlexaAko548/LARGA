using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using FirebaseAdmin.Auth;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Bridges Firebase sign-in (which happens in the browser) to the ASP.NET cookie session.
///
/// Blazor Server components can't set cookies, so login hands off to the /auth/signin HTTP endpoint.
/// That endpoint must never trust a uid from the URL. Instead:
///   1. The Login page sends the Firebase ID token here; it is verified with the Admin SDK and the
///      Manager role is checked server-side against users/{uid}.role.
///   2. On success a random, single-use ticket valid for 60 seconds is issued.
///   3. /auth/signin redeems the ticket (once) and issues the cookie.
///
/// After sign-in, access is re-checked server-side every <see cref="RecheckInterval"/> (account
/// still enabled in Firebase, users/{uid}.role still the role the session was issued with): on page requests by the cookie's
/// OnValidatePrincipal (ManagerSessionValidator) and on open Blazor circuits by
/// ManagerRevalidatingAuthStateProvider. So a manager whose access is removed is signed out
/// within that interval instead of keeping it until the 8-hour cookie expires.
/// </summary>
public class ManagerSignInService
{
    public const string ManagerRole = "Manager";
    public const string AssistantManagerRole = "Assistant Manager";

    /// <summary>Roles that may sign in to the web portal. Pages not limited by <see cref="ManagerOnlyPolicy"/> are open to both.</summary>
    private static readonly string[] StaffRoles = [ManagerRole, AssistantManagerRole];

    /// <summary>Policy for pages only a full Manager may open (Audit Logs, Inventory).</summary>
    public const string ManagerOnlyPolicy = "ManagerOnly";

    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(60);

    /// <summary>How often a signed-in manager's access is checked again.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(10);

    /// <summary>Cookie claim: when access was last confirmed (Unix seconds, UTC).</summary>
    public const string ValidatedAtClaim = "larga:validated_at";

    /// <summary>Result of a re-check. Unknown = Firebase/Firestore couldn't be reached; the
    /// session is kept and checked again later, so a network blip doesn't sign everyone out.</summary>
    public enum AccessCheck { Allowed, Revoked, Unknown }

    private readonly Lazy<FirebaseAuth> _firebaseAuth;
    private readonly Lazy<FirestoreDb> _firestore;
    private readonly ILogger<ManagerSignInService> _logger;
    private readonly ConcurrentDictionary<string, (ClaimsPrincipal Principal, DateTimeOffset ExpiresAt)> _tickets = new();

    // uids found revoked by a re-check, so every later request for them is refused at once - even
    // when the circuit found it first and the cookie's own re-check isn't due yet. Cleared when
    // the account signs in successfully again.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revokedUids = new();

    public ManagerSignInService(Lazy<FirebaseAuth> firebaseAuth, Lazy<FirestoreDb> firestore, ILogger<ManagerSignInService> logger)
    {
        _firebaseAuth = firebaseAuth;
        _firestore = firestore;
        _logger = logger;
    }

    /// <summary>Verifies the ID token and Manager role. Returns a sign-in ticket, or an error message.</summary>
    public async Task<(string? Ticket, string? Error)> CreateTicketAsync(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return (null, "Sign-in failed: no Firebase ID token was provided.");
        }

        FirebaseToken token;
        try
        {
            // checkRevoked: tokens of disabled users or revoked sessions are rejected too.
            token = await _firebaseAuth.Value.VerifyIdTokenAsync(idToken, checkRevoked: true);
        }
        catch (FirebaseAuthException ex)
        {
            _logger.LogWarning(ex, "Rejected Firebase ID token during manager sign-in.");
            return (null, "Your sign-in could not be verified. Please try again.");
        }

        string? email = token.Claims.TryGetValue("email", out var emailClaim) ? emailClaim as string : null;

        string? role = await StaffRoleAsync(token.Uid, email);
        if (role is null)
        {
            return (null, "This account is not authorized to access the manager portal.");
        }

        _revokedUids.TryRemove(token.Uid, out _);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, token.Uid),
            new(ClaimTypes.Role, role),
            new(ValidatedAtClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };
        if (!string.IsNullOrWhiteSpace(email))
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
            claims.Add(new Claim(ClaimTypes.Name, email));
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

        PruneExpiredTickets();
        string ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _tickets[ticket] = (principal, DateTimeOffset.UtcNow.Add(TicketLifetime));
        return (ticket, null);
    }

    /// <summary>Redeems a ticket exactly once. Returns null if it is unknown, already used, or expired.</summary>
    public ClaimsPrincipal? RedeemTicket(string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket) || !_tickets.TryRemove(ticket, out var entry))
        {
            return null;
        }
        return entry.ExpiresAt > DateTimeOffset.UtcNow ? entry.Principal : null;
    }

    /// <summary>The portal role (<see cref="ManagerRole"/> or <see cref="AssistantManagerRole"/>), or null if the account has neither.</summary>
    private async Task<string?> StaffRoleAsync(string uid, string? email)
    {
        try
        {
            return await LookupStaffRoleAsync(uid, email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manager role lookup failed for uid {Uid}.", uid);
            return null;
        }
    }

    // Same lookup as getManagerRole() in wwwroot/firebase-auth.js: users/{uid}, falling back to a
    // users doc whose email matches (email comes from the verified token, not from the client).
    // Throws when Firestore can't be reached.
    private async Task<string?> LookupStaffRoleAsync(string uid, string? email)
    {
        CollectionReference users = _firestore.Value.Collection("users");
        DocumentSnapshot profile = await users.Document(uid).GetSnapshotAsync();

        if (!profile.Exists && !string.IsNullOrWhiteSpace(email))
        {
            QuerySnapshot matches = await users.WhereEqualTo("email", email).Limit(1).GetSnapshotAsync();
            profile = matches.Documents.FirstOrDefault() ?? profile;
        }

        if (!profile.Exists || !profile.TryGetValue("role", out string role))
        {
            return null;
        }
        return StaffRoles.FirstOrDefault(r => string.Equals(r, role?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the session's access is due for a re-check (or was never stamped).</summary>
    public static bool NeedsRecheck(ClaimsPrincipal principal)
    {
        string? stamp = principal.FindFirst(ValidatedAtClaim)?.Value;
        return !long.TryParse(stamp, out long seconds)
            || DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds) >= RecheckInterval;
    }

    /// <summary>The same principal with its re-check time set to now.</summary>
    public static ClaimsPrincipal WithFreshValidation(ClaimsPrincipal principal)
    {
        var identity = new ClaimsIdentity(
            principal.Claims.Where(c => c.Type != ValidatedAtClaim),
            principal.Identity?.AuthenticationType ?? CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ValidatedAtClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()));
        return new ClaimsPrincipal(identity);
    }

    /// <summary>A uid a previous re-check found revoked (cheap - no Firebase call).</summary>
    public bool IsKnownRevoked(string uid) => _revokedUids.ContainsKey(uid);

    /// <summary>
    /// Re-checks a signed-in staff member: the Firebase account still exists and isn't disabled, and
    /// users/{uid}.role still equals the role the session was issued with. A role change (e.g. a
    /// Manager demoted to Assistant Manager) ends the session, so the next sign-in gets the new role.
    /// </summary>
    public async Task<AccessCheck> CheckAccessAsync(string? uid, string? email, string? sessionRole)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return AccessCheck.Revoked;
        }

        try
        {
            UserRecord user = await _firebaseAuth.Value.GetUserAsync(uid);
            string? currentRole = await LookupStaffRoleAsync(uid, email);
            if (user.Disabled || currentRole is null || !string.Equals(currentRole, sessionRole, StringComparison.Ordinal))
            {
                _revokedUids[uid] = DateTimeOffset.UtcNow;
                _logger.LogInformation("Manager access revoked for uid {Uid}; ending the session.", uid);
                return AccessCheck.Revoked;
            }

            return AccessCheck.Allowed;
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
            _revokedUids[uid] = DateTimeOffset.UtcNow;
            return AccessCheck.Revoked;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not re-check manager access for uid {Uid}; keeping the session for now.", uid);
            return AccessCheck.Unknown;
        }
    }

    private void PruneExpiredTickets()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, entry) in _tickets)
        {
            if (entry.ExpiresAt <= now) _tickets.TryRemove(key, out _);
        }
    }
}
