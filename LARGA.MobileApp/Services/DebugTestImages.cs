#if DEBUG
using System.IO;
using Microsoft.Maui.Storage;

namespace LARGA.MobileApp.Services;

/// <summary>Alpha-test hook (DEBUG builds only): a photo pushed to the emulator replaces the
/// camera. Checks /sdcard/Download first, then the app's own files folder (the app has no storage
/// permission, so Android 15 may hide /sdcard/Download from it).</summary>
internal static class DebugTestImages
{
    public static string? Find(string fileName)
    {
        foreach (string dir in new[] { "/sdcard/Download", FileSystem.AppDataDirectory })
        {
            try
            {
                string path = Path.Combine(dir, fileName);
                if (File.Exists(path)) return path;
            }
            catch { /* unreadable location: try the next one */ }
        }
        return null;
    }
}
#endif
