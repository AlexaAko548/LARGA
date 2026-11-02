#if DEBUG
using Android.Content;
using LARGA.MobileApp.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LARGA.MobileApp.Platforms.Android.Services;

/// <summary>
/// Alpha-test hook (DEBUG builds only): runs ML Kit on every photo in the app's external
/// files/ocr_batch folder - as is, then the OcrImageEnhancer copy, like the scan pages' second
/// pass - and logs each result as LARGA-OCR lines (AndroidOcrService), headed by
/// "LARGA-OCR file name=... pass=...". That logcat output is what LARGA.Tests/Fixtures/Fuel holds.
///
///   adb push photo.jpg /sdcard/Android/data/com.blmtaxi.larga.mobile/files/ocr_batch/
///   adb shell am broadcast -n com.blmtaxi.larga.mobile/com.blmtaxi.larga.mobile.OcrBatchReceiver
///
/// The app must be running (the work outlives the broadcast).
/// </summary>
[BroadcastReceiver(Name = "com.blmtaxi.larga.mobile.OcrBatchReceiver", Exported = true)]
public class OcrBatchReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        string? folder = context?.GetExternalFilesDir(null)?.AbsolutePath;
        if (folder == null) return;
        _ = Task.Run(() => RunAsync(Path.Combine(folder, "ocr_batch")));
    }

    private static async Task RunAsync(string folder)
    {
        var ocr = new AndroidOcrService();
        string[] photos = Directory.Exists(folder)
            ? Directory.GetFiles(folder).Where(IsPhoto).OrderBy(p => p, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

        foreach (string photo in photos)
        {
            string name = Path.GetFileName(photo);
            try
            {
                System.Diagnostics.Debug.WriteLine($"LARGA-OCR file name={name} pass=original");
                await ocr.ExtractTextBlocksAsync(photo);

                string? enhanced = OcrImageEnhancer.CreateEnhancedCopy(photo);
                if (enhanced == null) continue;
                try
                {
                    System.Diagnostics.Debug.WriteLine($"LARGA-OCR file name={name} pass=enhanced");
                    await ocr.ExtractTextBlocksAsync(enhanced);
                }
                finally
                {
                    File.Delete(enhanced);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LARGA-OCR file name={name} error={ex.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine($"LARGA-OCR batch done files={photos.Length}");
    }

    private static bool IsPhoto(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".jfif";
}
#endif
