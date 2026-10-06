using System;

namespace LARGA.MobileApp.Platforms.Android.Emergency;

/// <summary>
/// Logcat output for the LAR-86/87 emergency protocols under one tag, so a test run on a device
/// can be followed with <c>adb logcat -s LARGA-SOS</c> (Debug.WriteLine doesn't reach logcat).
/// </summary>
internal static class EmergencyLog
{
    private const string Tag = "LARGA-SOS";

    public static void Info(string message) => global::Android.Util.Log.Info(Tag, message);

    public static void Warn(string message) => global::Android.Util.Log.Warn(Tag, message);

    public static void Error(string message, Exception ex) =>
        global::Android.Util.Log.Error(Tag, $"{message}: {ex.GetType().Name}: {ex.Message}");
}
