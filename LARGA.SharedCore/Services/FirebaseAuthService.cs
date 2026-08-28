using System;
using System.Threading.Tasks;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;

namespace LARGA.SharedCore.Services;

public interface IFirebaseAuthService
{
    Task<string> LoginAsync(string email, string password);
    Task<string> GetUserRoleAsync(string userId);
}

public class FirebaseAuthService : IFirebaseAuthService
{
    public async Task<string> LoginAsync(string email, string password)
    {
        try
        {
            var user = await CrossFirebaseAuth.Current.SignInWithEmailAndPasswordAsync(email, password);
            return user.Uid;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Firebase Auth Error: {ex.Message}");
            return string.Empty;
        }
    }

    public async Task<string> GetUserRoleAsync(string userId)
    {
        try
        {
            // Deserialize using the mobile-specific proxy class
            var document = await CrossFirebaseFirestore.Current
                .GetCollection("users")
                .GetDocument(userId)
                .GetDocumentSnapshotAsync<FirestoreRoleProxy>();

            if (document?.Data != null)
            {
                return document.Data.Role ?? string.Empty;
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Firestore Deserialization Error: {ex.Message}");
            return string.Empty;
        }
    }
}

// Local proxy class that tells the mobile SDK exactly how to find the lowercase "role" field
public class FirestoreRoleProxy : IFirestoreObject
{
    [Plugin.Firebase.Firestore.FirestoreProperty("role")]
    public string Role { get; set; }
}