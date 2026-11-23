using System.Xml.Linq;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Keeps ASP.NET Core's data-protection keys (what encrypts the manager's login cookie and the
/// antiforgery tokens) in Firestore instead of the container's memory. On Cloud Run the app is
/// restarted whenever it scales to zero or is redeployed; with in-memory keys every manager would
/// be signed out each time. Stored in "dataprotection_keys", which firestore.rules don't open to
/// any client - only ManagerWeb's Admin SDK can read it.
/// </summary>
public sealed class FirestoreDataProtectionRepository : IXmlRepository
{
    private const string Collection = "dataprotection_keys";
    private readonly Lazy<FirestoreDb> _db;

    public FirestoreDataProtectionRepository(Lazy<FirestoreDb> db) => _db = db;

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        QuerySnapshot snapshot = _db.Value.Collection(Collection).GetSnapshotAsync().GetAwaiter().GetResult();
        return snapshot.Documents
            .Select(d => d.TryGetValue("xml", out string? xml) && !string.IsNullOrWhiteSpace(xml) ? XElement.Parse(xml) : null)
            .OfType<XElement>()
            .ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        string id = string.IsNullOrWhiteSpace(friendlyName) ? Guid.NewGuid().ToString("N") : friendlyName;
        _db.Value.Collection(Collection).Document(id)
            .SetAsync(new Dictionary<string, object>
            {
                ["xml"] = element.ToString(SaveOptions.DisableFormatting),
                ["createdAt"] = DateTime.UtcNow,
            })
            .GetAwaiter().GetResult();
    }
}
