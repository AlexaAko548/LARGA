using System;
using System.Threading.Tasks;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Core.Exceptions;
using Plugin.Firebase.Firestore;

namespace LARGA.SharedCore.Services;

public interface IFirebaseAuthService
{
    /// <summary>Signs in and returns the Firebase UID. Throws <see cref="AuthFailedException"/> on failure.</summary>
    Task<string> LoginAsync(string email, string password);

    /// <summary>Returns users/{uid}.role, or empty if the profile/field is missing. Throws on network/permission errors.</summary>
    Task<string> GetUserRoleAsync(string userId);

    /// <summary>users/{uid}.mustChangePassword (ForcedPasswordChangeRules). False when it's missing.</summary>
    Task<bool> MustChangePasswordAsync(string userId);

    Task<bool> SendPasswordResetEmailAsync(string email);
    Task SignOutAsync();
}

/// <summary>Login failure whose Message is safe to show to the user; the raw Firebase error is kept as InnerException.</summary>
public class AuthFailedException : Exception
{
    public AuthFailedException(string message, Exception? inner = null) : base(message, inner) { }
}

public class FirebaseAuthService : IFirebaseAuthService
{
    // Firebase calls that never complete (no Play Services, blocked network, bad google-services.json)
    // previously left the Login button looking dead. Fail loudly instead.
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    private const string NoConnectionMessage =
        "Can't reach the sign-in server. Check that this phone is online (Wi-Fi or mobile data), then try again.";

    public async Task<string> LoginAsync(string email, string password)
    {
        // No connection at all: say so right away instead of waiting for Firebase to give up.
        if (Microsoft.Maui.Networking.Connectivity.Current.NetworkAccess is Microsoft.Maui.Networking.NetworkAccess.None)
        {
            throw new AuthFailedException(NoConnectionMessage);
        }

        try
        {
            var user = await CrossFirebaseAuth.Current
                .SignInWithEmailAndPasswordAsync(email.Trim(), password)
                .WaitAsync(CallTimeout);
            return user.Uid;
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            System.Diagnostics.Debug.WriteLine($"Firebase Auth network error: {ex}");
            throw new AuthFailedException(NoConnectionMessage, ex);
        }
        catch (FirebaseAuthException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Firebase Auth Error ({ex.Reason}): {ex}");
            throw new AuthFailedException(ex.Reason switch
            {
                FIRAuthError.InvalidEmail => "That email address is not valid.",
                FIRAuthError.WrongPassword or FIRAuthError.InvalidCredential or FIRAuthError.UserNotFound
                    => "Incorrect email or password.",
                FIRAuthError.UserDisabled => "This account has been disabled. Please contact your manager.",
                _ => $"Sign-in failed: {ex.Message}"
            }, ex);
        }
        catch (TimeoutException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Firebase Auth Timeout: {ex}");
            throw new AuthFailedException(NoConnectionMessage, ex);
        }
        catch (Exception ex)
        {
            // Covers FirebaseApp not initialized, missing google-services.json, network errors, etc.
            System.Diagnostics.Debug.WriteLine($"Firebase Auth Error: {ex}");
            throw new AuthFailedException($"Sign-in failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Firebase's "A network error (such as timeout, interrupted connection or unreachable host)"
    /// - Android's FirebaseNetworkException, which Plugin.Firebase doesn't give its own error code.
    /// The phone couldn't reach Google at all (offline, captive Wi-Fi, a stuck emulator network).
    /// </summary>
    private static bool IsNetworkFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e.GetType().Name.Contains("FirebaseNetworkException", StringComparison.Ordinal)
                || e.Message.Contains("network error", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("unreachable host", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public async Task<string> GetUserRoleAsync(string userId)
    {
        // Deserialize using the mobile-specific proxy class. Errors (permission-denied, offline,
        // timeout) propagate so callers can surface them instead of reporting a bogus "no role".
        var document = await CrossFirebaseFirestore.Current
            .GetCollection("users")
            .GetDocument(userId)
            .GetDocumentSnapshotAsync<FirestoreRoleProxy>()
            .WaitAsync(CallTimeout);

        return document?.Data?.Role ?? string.Empty;
    }

    public async Task<bool> MustChangePasswordAsync(string userId)
    {
        var document = await CrossFirebaseFirestore.Current
            .GetCollection("users")
            .GetDocument(userId)
            .GetDocumentSnapshotAsync<FirestoreRoleProxy>()
            .WaitAsync(CallTimeout);

        return document?.Data?.MustChangePassword == true;
    }

    public Task SignOutAsync() => CrossFirebaseAuth.Current.SignOutAsync();

    public async Task<bool> SendPasswordResetEmailAsync(string email)
    {
        try
        {
            await Plugin.Firebase.Auth.CrossFirebaseAuth.Current.SendPasswordResetEmailAsync(email);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Firebase Password Reset Error: {ex.Message}");
            return false;
        }
    }
}

// Local proxy class that tells the mobile SDK exactly how to find the lowercase "role" field
public class FirestoreRoleProxy : IFirestoreObject
{
    [Plugin.Firebase.Firestore.FirestoreProperty("role")]
    public string Role { get; set; }

    [Plugin.Firebase.Firestore.FirestoreProperty("mustChangePassword")]
    public bool MustChangePassword { get; set; }
}