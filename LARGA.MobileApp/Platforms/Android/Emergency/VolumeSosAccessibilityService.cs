using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Views;
using Android.Views.Accessibility;
using LARGA.SharedCore.Emergency;
using AndroidApp = Android.App.Application;

namespace LARGA.MobileApp.Platforms.Android.Emergency;

/// <summary>
/// Hostile Protocol trigger from the volume buttons (<see cref="VolumePatternDetector"/>: down,
/// up, down, up within 3 s). An accessibility service is the one way an app sees volume presses
/// while it's in the background or the screen is off, without taking the keys over. The driver
/// switches it on once per phone (Settings &gt; Accessibility &gt; LARGA Safety Buttons; on Android
/// 13+ an APK install first needs "Allow restricted settings" in the app's info screen).
/// It reads nothing on screen - key events only - and never consumes a press, so the volume still
/// changes as usual. Presses outside a shift are ignored (<see cref="EmergencyDetectionService"/>
/// isn't monitoring).
/// </summary>
[Service(
    Name = "com.blmtaxi.larga.mobile.VolumeSosAccessibilityService",
    Label = "LARGA Safety Buttons",
    Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE",
    Exported = true)]
[IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
[MetaData("android.accessibilityservice", Resource = "@xml/volume_sos_accessibility")]
public sealed class VolumeSosAccessibilityService : AccessibilityService
{
    private readonly VolumePatternDetector _detector = new();

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        // Not used: the service only listens to key events.
    }

    public override void OnInterrupt()
    {
    }

    protected override bool OnKeyEvent(KeyEvent? e)
    {
        // Key-down only, and no auto-repeat from a button held down.
        if (e == null || e.Action != KeyEventActions.Down || e.RepeatCount > 0)
        {
            return false;
        }

        bool up;
        if (e.KeyCode == Keycode.VolumeUp) up = true;
        else if (e.KeyCode == Keycode.VolumeDown) up = false;
        else return false;

        if (_detector.OnPress(e.EventTime / 1000.0, up) && !EmergencyDetectionService.HandleVolumePattern())
        {
            EmergencyLog.Info("Volume-button pattern ignored: no shift is being monitored.");
        }

        return false; // never swallow the press: the volume changes as usual, so nothing looks different
    }

    /// <summary>True when the driver has switched the service on in Accessibility settings.</summary>
    public static bool IsEnabled()
    {
        Context context = AndroidApp.Context;
        string? enabled = global::Android.Provider.Settings.Secure.GetString(
            context.ContentResolver, global::Android.Provider.Settings.Secure.EnabledAccessibilityServices);
        if (string.IsNullOrEmpty(enabled)) return false;

        string ours = new ComponentName(context, Java.Lang.Class.FromType(typeof(VolumeSosAccessibilityService))).FlattenToString();
        foreach (string service in enabled.Split(':'))
        {
            if (string.Equals(service, ours, System.StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
