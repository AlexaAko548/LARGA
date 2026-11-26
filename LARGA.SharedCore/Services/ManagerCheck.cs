using System.Threading.Tasks;
using Google.Cloud.Firestore;

namespace LARGA.SharedCore.Services;

/// <summary>Whether the person doing a Manager-only action really is the Manager. ManagerWeb writes with
/// the Admin SDK, which skips firestore.rules, so the service asks for itself.</summary>
public static class ManagerCheck
{
    public static async Task<bool> IsManagerAsync(FirestoreDb db, string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        DocumentSnapshot user = await db.Collection("users").Document(userId).GetSnapshotAsync();
        return user.Exists && user.TryGetValue("role", out string? role) && StaffRoles.IsManager(role);
    }
}
