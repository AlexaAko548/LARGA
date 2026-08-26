using System;
using System.Threading.Tasks;
using LARGA.MobileApp.ViewModels;

namespace LARGA.MobileApp.Services;

public class FirebaseAuthService : IFirebaseAuthService
{
    public Task<string> LoginAsync(string email, string password)
    {
        // Placeholder real integration point for Firebase Authentication.
        // For demonstration, a fake successful token is returned.
        if (email == "driver@larga.com" && password == "password")
            return Task.FromResult("driver-user-id");
        if (email == "manager@larga.com" && password == "password")
            return Task.FromResult("manager-user-id");

        return Task.FromResult(string.Empty);
    }

    public Task<string> GetUserRoleAsync(string userId)
    {
        // Placeholder logic to query Firestore claims/roles.
        if (userId == "driver-user-id")
            return Task.FromResult("Driver");
        if (userId == "manager-user-id")
            return Task.FromResult("Manager");

        return Task.FromResult("Unknown");
    }
}
