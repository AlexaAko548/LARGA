using System.Threading.Tasks;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Fallback <see cref="IFaceCropService"/> for platforms without ML Kit (Windows, MacCatalyst).
/// Reports "no face found", so the license save still works and the profile keeps its
/// initials avatar. Same reasoning as <see cref="UnsupportedOcrService"/>: an empty result is
/// handled, a throw would fail the whole save.
/// </summary>
public class UnsupportedFaceCropService : IFaceCropService
{
    public Task<string?> CropDriverFaceAsync(string localFilePath) => Task.FromResult<string?>(null);
}
