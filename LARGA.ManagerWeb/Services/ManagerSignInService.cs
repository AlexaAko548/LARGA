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
/// </summary>
public class ManagerSignInService
{
    public const string ManagerRole = "Manager";
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(60);

    private readonly Lazy<FirebaseAuth> _firebaseAuth;
    private readonly Lazy<FirestoreDb> _firestore;
    private readonly ILogger<ManagerSignInService> _logger;
    private readonly ConcurrentDictionary<string, (ClaimsPrincipal Principal, DateTimeOffset ExpiresAt)> _tickets = new();

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

        if (!await IsManagerAsync(token.Uid, email))
        {
            return (null, "This account is not authorized to access the manager portal.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, token.Uid),
            new(ClaimTypes.Role, ManagerRole)
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

    // Same lookup as getManagerRole() in wwwroot/firebase-auth.js: users/{uid}, falling back to a
    // users doc whose email matches (email comes from the verified token, not from the client).
    private async Task<bool> IsManagerAsync(string uid, string? email)
    {
        try
        {
            CollectionReference users = _firestore.Value.Collection("users");
            DocumentSnapshot profile = await users.Document(uid).GetSnapshotAsync();

            if (!profile.Exists && !string.IsNullOrWhiteSpace(email))
            {
                QuerySnapshot matches = await users.WhereEqualTo("email", email).Limit(1).GetSnapshotAsync();
                profile = matches.Documents.FirstOrDefault() ?? profile;
            }

            return profile.Exists
                && profile.TryGetValue("role", out string role)
                && string.Equals(role?.Trim(), ManagerRole, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manager role lookup failed for uid {Uid}.", uid);
            return false;
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
