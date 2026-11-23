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
                if (!File.Exists(path)) continue;
                // Android 15 can list a file in /sdcard/Download that the app may not read.
                using (File.OpenRead(path)) { }
                return path;
            }
            catch { /* unreadable location: try the next one */ }
        }
        return null;
    }
}
#endif
