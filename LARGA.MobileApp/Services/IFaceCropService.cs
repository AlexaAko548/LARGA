using System.Threading.Tasks;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Finds the face on a driver's license photo and writes a square, face-centred crop to a
/// cache file. The crop becomes the driver's profile picture (users/{id}.profileImageUrl).
/// </summary>
public interface IFaceCropService
{
    /// <summary>Returns the path of a cropped JPEG, or null when no face is found or the
    /// platform has no face detector.</summary>
    Task<string?> CropDriverFaceAsync(string localFilePath);
}
