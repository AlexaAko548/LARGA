using FirebaseAdmin.Auth;
using Google.Cloud.Firestore;
using LARGA.SharedCore.Models.ManagerProfile;
using Microsoft.AspNetCore.Components.Authorization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace LARGA.ManagerWeb.Services
{
    public interface IManagerAuthService
    {
        Task<(bool Success, string ErrorMessage)> UpdateContactNumberAsync(UpdateContactModel model);
        Task<(bool Success, string ErrorMessage)> UpdatePasswordAsync(UpdatePasswordModel model);
    }

    public class ManagerAuthService : IManagerAuthService
    {
        private readonly Lazy<FirestoreDb> _firestoreDb;
        private readonly Lazy<FirebaseAuth> _firebaseAuth;
        private readonly AuthenticationStateProvider _authStateProvider;

        public ManagerAuthService(
            Lazy<FirestoreDb> firestoreDb,
            Lazy<FirebaseAuth> firebaseAuth,
            AuthenticationStateProvider authStateProvider)
        {
            _firestoreDb = firestoreDb;
            _firebaseAuth = firebaseAuth;
            _authStateProvider = authStateProvider;
        }

        // Upgraded diagnostic method to find the exact reason the User ID is missing
        private async Task<(bool IsAuthenticated, string Uid, string ErrorMsg)> GetCurrentUserContextAsync()
        {
            try
            {
                var authState = await _authStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                // 1. Check if Blazor actually recognizes the user as logged in
                if (user?.Identity?.IsAuthenticated != true)
                {
                    return (false, string.Empty, "Session invalid: Blazor AuthenticationStateProvider says you are not authenticated. Check your Login provider setup.");
                }

                // 2. Try to find the Firebase UID in standard claims (added "uid" check)
                string uid = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? user.FindFirst("uid")?.Value
                    ?? user.FindFirst("user_id")?.Value
                    ?? user.FindFirst("sub")?.Value;

                if (!string.IsNullOrEmpty(uid))
                {
                    return (true, uid, string.Empty);
                }

                // 3. If authenticated but UID is missing, list the claims that DO exist
                var existingClaims = string.Join(", ", user.Claims.Select(c => c.Type));
                return (true, string.Empty, $"Logged in, but UID claim is missing. Available claims: [{existingClaims}]");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error retrieving current user ID: {ex.Message}");
                return (false, string.Empty, $"Auth context error: {ex.Message}");
            }
        }

        public async Task<(bool Success, string ErrorMessage)> UpdateContactNumberAsync(UpdateContactModel model)
        {
            try
            {
                var authContext = await GetCurrentUserContextAsync();
                if (!authContext.IsAuthenticated || string.IsNullOrEmpty(authContext.Uid))
                    return (false, authContext.ErrorMsg);

                var db = _firestoreDb.Value;
                DocumentReference docRef = db.Collection("users").Document(authContext.Uid);

                await docRef.UpdateAsync(new Dictionary<string, object>
                {
                    { "contactNumber", model.NewContactNumber }
                });

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Firestore Update Contact Error: {ex.Message}");
                if (ex is OperationCanceledException)
                    return (false, "Connection timed out. Check if your Firebase CredentialsPath is correct in appsettings.");

                return (false, ex.Message);
            }
        }

        public async Task<(bool Success, string ErrorMessage)> UpdatePasswordAsync(UpdatePasswordModel model)
        {
            try
            {
                var authContext = await GetCurrentUserContextAsync();
                if (!authContext.IsAuthenticated || string.IsNullOrEmpty(authContext.Uid))
                    return (false, authContext.ErrorMsg);

                var auth = _firebaseAuth.Value;

                var args = new UserRecordArgs
                {
                    Uid = authContext.Uid,
                    Password = model.NewPassword
                };

                await auth.UpdateUserAsync(args);
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Firebase Admin Update Password Error: {ex.Message}");

                if (ex is OperationCanceledException)
                    return (false, "Firebase Admin SDK timed out. Ensure 'Firestore:CredentialsPath' is correctly set to your service account JSON file in appsettings.");

                return (false, ex.Message);
            }
        }

    }
}