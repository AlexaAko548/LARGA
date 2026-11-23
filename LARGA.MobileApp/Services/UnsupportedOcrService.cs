using System.Collections.Generic;
using System.Threading.Tasks;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Fallback <see cref="IOcrService"/> for every platform other than Android (Windows,
/// MacCatalyst) - ML Kit Text Recognition is only wired up for Android
/// (<see cref="MauiProgram"/> only registers <c>AndroidOcrService</c> inside
/// <c>#if ANDROID</c>), so without this, <c>IOcrService</c> had no registration at all on
/// those platforms. Any page that takes it as a constructor DI dependency (e.g.
/// ManagerDriverProfilePage's license-scan flow, PreShiftStep2ViewModel/
/// EndShiftStep2ViewModel) would fail to resolve and crash on open; any page that fetches it
/// manually via <c>GetService&lt;IOcrService&gt;()</c> (ScanFuelReceiptPage's
/// receipt-upload flow) got back null and threw a NullReferenceException the moment OCR ran.
/// Both are real workflows the team hits - Visual Studio's Windows debug target is used
/// for quick UI iteration without an Android device/emulator attached (see the
/// Microsoft.Maui.Controls.Compatibility PRI175/PRI277 fix, which exists for the same reason).
///
/// Returning an empty result here (rather than throwing) is deliberate: every OCR call site
/// already treats "nothing detected" as a normal, handled case - license verification shows
/// "couldn't read that clearly" and lets the user retake/retry, and the fuel receipt scanner
/// falls back to "--"/UNKNOWN placeholders with a parsing-uncertain warning so the driver can
/// type the values in by hand. On Windows/MacCatalyst, OCR simply never finds anything;
/// nothing crashes, and manual entry still works end-to-end.
/// </summary>
public class UnsupportedOcrService : IOcrService
{
    public Task<List<OcrTextBlock>> ExtractTextBlocksAsync(string localFilePath)
    {
        System.Diagnostics.Debug.WriteLine(
            "OCR requested but is not supported on this platform (Android-only via ML Kit). Returning no text blocks.");
        return Task.FromResult(new List<OcrTextBlock>());
    }
}
