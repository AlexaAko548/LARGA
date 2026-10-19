using System;
using System.Collections.Generic;
using Microsoft.Maui.Networking;

namespace LARGA.MobileApp.Services;

/// <summary>What kind of failure a Firestore call hit, so a screen can say something useful instead of one
/// generic message.</summary>
public enum FirestoreErrorKind
{
    Offline,
    PermissionDenied,
    SignedOut,
    MissingIndex,
    Other,
}

/// <summary>
/// Turns a Firestore exception into a <see cref="FirestoreErrorKind"/> and writes the whole exception to the log.
///
/// Plugin.Firebase rethrows the native Android exception (FirebaseFirestoreException), sometimes wrapped. Its
/// message starts with the Firestore status code ("PERMISSION_DENIED: Missing or insufficient permissions."), so
/// the chain's messages are matched rather than one binding type.
/// </summary>
public static class FirestoreErrors
{
    // logcat tag. Filter with: adb logcat -v time | findstr LARGA
    private const string Tag = "LARGA";

    public static FirestoreErrorKind Classify(Exception ex)
    {
        string text = Describe(ex);

        if (Contains(text, "PERMISSION_DENIED", "Missing or insufficient permissions")) return FirestoreErrorKind.PermissionDenied;
        if (Contains(text, "UNAUTHENTICATED")) return FirestoreErrorKind.SignedOut;
        if (Contains(text, "FAILED_PRECONDITION", "requires an index")) return FirestoreErrorKind.MissingIndex;
        if (ex is TimeoutException
            || Contains(text, "UNAVAILABLE", "DEADLINE_EXCEEDED", "UnknownHostException", "SocketTimeoutException",
                        "ConnectException", "Failed to get document because the client is offline", "timed out")
            || IsOffline())
        {
            return FirestoreErrorKind.Offline;
        }

        return FirestoreErrorKind.Other;
    }

    /// <summary>Writes the full exception chain (type, message, Firestore code) under the LARGA tag. Unlike
    /// Debug.WriteLine, this is kept in Release builds, so a manager's phone can be checked with adb logcat.</summary>
    public static void Log(string where, Exception ex)
    {
        string message = $"{where} failed ({Classify(ex)}): {Describe(ex)}\n{ex.StackTrace}";
#if ANDROID
        Android.Util.Log.Error(Tag, message);
#else
        System.Diagnostics.Debug.WriteLine($"[{Tag}] {message}");
#endif
    }

    /// <summary>Every exception in the chain as "Type: message", outermost first.</summary>
    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        var seen = new HashSet<Exception>();
        for (Exception? current = ex; current is not null && seen.Add(current); current = current.InnerException)
        {
            parts.Add($"{current.GetType().FullName}: {current.Message}");
        }

        return string.Join(" --> ", parts);
    }

    private static bool IsOffline()
    {
        try
        {
            return Connectivity.Current.NetworkAccess != NetworkAccess.Internet;
        }
        catch
        {
            return false;
        }
    }

    private static bool Contains(string text, params string[] needles)
    {
        foreach (string needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
