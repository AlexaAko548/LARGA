using System;
using System.Threading.Tasks;
using Plugin.Firebase.Storage;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Uploads driver-captured evidence photos (fuel level, odometer dashboard, and eventually
/// defect-report photos) to Firebase Cloud Storage from the mobile client. Same
/// CrossFirebase*.Current client-SDK pattern as ChatService/ShiftManagementService - this
/// authenticates as the signed-in driver via Firebase Auth and is subject to Storage Security
/// Rules, not the admin SDK used by ManagerWeb/SeedTool.
/// </summary>
public interface IPhotoStorageService
{
    /// <summary>
    /// Uploads a photo and returns its long-lived download URL, or null if the upload failed.
    /// Never throws - a failed photo upload shouldn't block the pre-/end-shift flow it's part
    /// of, so callers treat a null result as "couldn't attach evidence" and carry on.
    /// </summary>
    /// <param name="storagePath">Full object path, e.g. "fuel_photos/{driverId}/{fileName}.jpg".</param>
    Task<string?> UploadPhotoAsync(string storagePath, byte[] photoBytes, string contentType = "image/jpeg");
}

public class PhotoStorageService : IPhotoStorageService
{
    public async Task<string?> UploadPhotoAsync(string storagePath, byte[] photoBytes, string contentType = "image/jpeg")
    {
        try
        {
            IStorageReference reference = CrossFirebaseStorage.Current.GetRootReference().GetChild(storagePath);
            var metadata = new StorageMetadata(contentType: contentType);
            await reference.PutBytes(photoBytes, metadata).AwaitAsync();
            return await reference.GetDownloadUrlAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Photo Upload Error: {ex.Message}");
            return null;
        }
    }
}
