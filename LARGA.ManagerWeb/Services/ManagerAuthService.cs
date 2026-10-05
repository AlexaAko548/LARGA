using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.ManagerWeb.Models;
using LARGA.SharedCore;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace LARGA.ManagerWeb.Services
{
    public interface IManagerAuthService
    {
        Task<(bool Success, string ErrorMessage)> UpdateContactNumberAsync(UpdateContactModel model);
    }

    /// <summary>
    /// The signed-in manager's own profile changes from the header's profile menu. A password
    /// change isn't here: it has to prove the current password, which only Firebase sign-in can
    /// do, so the profile menu runs it in the browser (firebase-auth.js largaFirebaseChangePassword).
    /// </summary>
    public class ManagerAuthService : IManagerAuthService
    {
        private readonly Lazy<FirestoreDb> _firestoreDb;
        private readonly AuthenticationStateProvider _authStateProvider;
        private readonly ILogger<ManagerAuthService> _logger;

        public ManagerAuthService(
            Lazy<FirestoreDb> firestoreDb,
            AuthenticationStateProvider authStateProvider,
            ILogger<ManagerAuthService> logger)
        {
            _firestoreDb = firestoreDb;
            _authStateProvider = authStateProvider;
            _logger = logger;
        }

        private async Task<string?> GetCurrentUidAsync()
        {
            AuthenticationState authState = await _authStateProvider.GetAuthenticationStateAsync();
            return authState.User.Identity?.IsAuthenticated == true
                ? authState.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                : null;
        }

        /// <summary>
        /// Same rules as the mobile app's Update Contact Number: the current number must match the
        /// one on file, the new one must be a Philippine mobile number, and it's saved to
        /// users/{uid}.phoneNumber as +639XXXXXXXXX.
        /// </summary>
        public async Task<(bool Success, string ErrorMessage)> UpdateContactNumberAsync(UpdateContactModel model)
        {
            if (!InputValidator.SamePhone(model.NewContactNumber, model.ConfirmContactNumber))
            {
                return (false, "The new number and its confirmation don't match.");
            }

            if (InputValidator.ValidatePhilippineMobile(model.NewContactNumber) is string phoneError)
            {
                return (false, phoneError);
            }

            try
            {
                string? uid = await GetCurrentUidAsync();
                if (string.IsNullOrEmpty(uid))
                {
                    return (false, "Your session has expired. Please sign in again.");
                }

                DocumentReference docRef = _firestoreDb.Value.Collection("users").Document(uid);
                DocumentSnapshot snapshot = await docRef.GetSnapshotAsync();
                string onFile = snapshot.Exists && snapshot.TryGetValue("phoneNumber", out string phone) ? phone : string.Empty;

                if (!InputValidator.SamePhone(onFile, model.CurrentContactNumber))
                {
                    return (false, "That doesn't match the number currently on file.");
                }

                await docRef.UpdateAsync(new Dictionary<string, object>
                {
                    ["phoneNumber"] = InputValidator.NormalizePhilippineMobile(model.NewContactNumber)!,
                });

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update the manager's contact number.");
                return (false, "Couldn't update your contact number. Please try again.");
            }
        }
    }
}
