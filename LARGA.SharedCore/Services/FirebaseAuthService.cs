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

    public async Task<string> LoginAsync(string email, string password)
    {
        try
        {
            var user = await CrossFirebaseAuth.Current
                .SignInWithEmailAndPasswordAsync(email.Trim(), password)
                .WaitAsync(CallTimeout);
            return user.Uid;
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
            throw new AuthFailedException("Could not reach the sign-in server. Check this device's network connection and try again.", ex);
        }
        catch (Exception ex)
        {
            // Covers FirebaseApp not initialized, missing google-services.json, network errors, etc.
            System.Diagnostics.Debug.WriteLine($"Firebase Auth Error: {ex}");
            throw new AuthFailedException($"Sign-in failed: {ex.Message}", ex);
        }
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