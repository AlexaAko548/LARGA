using System;
using System.IO;
using System.Linq;
using Microsoft.Maui.Storage;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Alpha-test hook (DEBUG Android builds only) for the license scan, whose camera can't be fed
/// a real card on the emulator: photos queued in the app's external files/debug_camera folder
/// are used, one per "take photo", in name order, instead of opening the camera.
///
///   adb push license.jpg /sdcard/Android/data/com.blmtaxi.larga.mobile/files/debug_camera/1.jpg
///
/// Each photo is moved out of the queue when taken. Release builds always use the camera.
/// </summary>
public static class DebugCameraSource
{
    /// <summary>Path of the next queued photo, now in the cache folder; null when none is
    /// queued (or in a release build) - open the camera as usual.</summary>
    public static string? TakeQueued()
    {
#if DEBUG && ANDROID
        try
        {
            string? root = global::Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
            string folder = Path.Combine(root ?? string.Empty, "debug_camera");
            if (root == null || !Directory.Exists(folder)) return null;

            string? next = Directory.GetFiles(folder).OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
            if (next == null) return null;

            string target = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}_{Path.GetFileName(next)}");
            File.Move(next, target);
            System.Diagnostics.Debug.WriteLine($"LARGA-CAM using queued photo {Path.GetFileName(next)}");
            return target;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LARGA-CAM queued photo failed: {ex.Message}");
        }
#endif
        return null;
    }
}
