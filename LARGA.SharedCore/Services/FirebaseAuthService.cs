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

    /// <summary>Returns users/{uid}.role: empty when the profile has no role, null when there is no
    /// LARGA profile at all. Throws on network/permission errors.</summary>
    Task<string?> GetUserRoleAsync(string userId);

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

    /// <summary>
    /// Wrong password, unknown email, or a sign-in with no LARGA profile behind it (DSM-004/005/007).
    /// One message for all of them on purpose: saying which part was wrong would tell a stranger
    /// which emails have accounts - and Firebase itself no longer says which (email enumeration
    /// protection), so the app couldn't tell "no account" from "wrong password" anyway.
    /// </summary>
    public const string IncorrectCredentialsMessage =
        "Incorrect email or password. Check your details, or contact your manager if you don't have an account yet.";

    public const string DisabledAccountMessage = "This account has been disabled. Please contact your manager.";

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
            // DSM-016: on Android, Plugin.Firebase reports every FirebaseAuthInvalidUserException as
            // UserNotFound - a disabled account included - so the original error is checked too.
            if (ex.Reason == FIRAuthError.UserDisabled || IsDisabledAccount(ex))
            {
                throw new AuthFailedException(DisabledAccountMessage, ex);
            }

            throw new AuthFailedException(ex.Reason switch
            {
                FIRAuthError.InvalidEmail => "Enter a valid email address, e.g. juan.delacruz@gmail.com.",
                FIRAuthError.WrongPassword or FIRAuthError.InvalidCredential or FIRAuthError.UserNotFound
                    => IncorrectCredentialsMessage,
                _ => "Sign-in failed. Please try again, or contact your manager if it keeps happening."
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
            if (IsDisabledAccount(ex))
            {
                throw new AuthFailedException(DisabledAccountMessage, ex);
            }
            throw new AuthFailedException("Sign-in failed. Please try again, or contact your manager if it keeps happening.", ex);
        }
    }

    /// <summary>Firebase's "ERROR_USER_DISABLED" / "The user account has been disabled by an
    /// administrator.", wherever it sits in the exception chain.</summary>
    private static bool IsDisabledAccount(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains("USER_DISABLED", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("has been disabled", StringComparison.OrdinalIgnoreCase)
                || e.GetType().GetProperty("ErrorCode")?.GetValue(e) is string code && code.Contains("USER_DISABLED", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
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

    public async Task<string?> GetUserRoleAsync(string userId)
    {
        // Deserialize using the mobile-specific proxy class. Errors (permission-denied, offline,
        // timeout) propagate so callers can surface them instead of reporting a bogus "no role".
        var document = await CrossFirebaseFirestore.Current
            .GetCollection("users")
            .GetDocument(userId)
            .GetDocumentSnapshotAsync<FirestoreRoleProxy>()
            .WaitAsync(CallTimeout);

        // No document = no LARGA profile at all (null); a profile without a role = "".
        return document?.Data is null ? null : document.Data.Role ?? string.Empty;
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
}