using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
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

    /// <summary>
    /// A message a driver or manager can act on, for an action that failed ("start your shift"). Connection and
    /// permission problems get their own wording; an InvalidOperationException is one of the app's own rule messages
    /// (written for the user) and is shown as is; anything else gets a plain "try again" instead of a raw error.
    /// Call <see cref="Log"/> as well so the details are kept.
    /// </summary>
    public static string FriendlyMessage(Exception ex, string action) => Classify(ex) switch
    {
        FirestoreErrorKind.Offline => $"Could not {action}: no connection to the server. Check your internet connection and try again.",
        FirestoreErrorKind.PermissionDenied => $"Could not {action}: the server refused it. Log out and log in again. If it keeps happening, call your manager.",
        FirestoreErrorKind.SignedOut => $"Could not {action}: your session has expired. Please log out and log in again.",
        _ when ex is InvalidOperationException => ex.Message,
        _ => $"Could not {action}. Please try again. If it keeps happening, call your manager.",
    };

    /// <summary>A screen's data didn't load: log it and tell the user, instead of showing an empty or zero screen as
    /// if that were the real data. Safe to call from a background thread.</summary>
    public static Task ShowLoadFailureAsync(string where, Exception ex, string what, bool canPullToRefresh)
    {
        Log(where, ex);
        string message = FriendlyMessage(ex, $"load {what}")
            + (canPullToRefresh ? " Pull down to try again." : " Go back and open this screen again to retry.");
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (Shell.Current is Shell shell)
            {
                await shell.DisplayAlert("Couldn't load", message, "OK");
            }
        });
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
