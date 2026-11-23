using System;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Shell.Current.DisplayAlert, but safe to call from inside a catch block. If the original
/// exception came from something like Shell.Current itself being transiently null, a bare
/// DisplayAlert there would throw again and still crash the app (BETA-062 review follow-up) -
/// this swallows that one.
/// </summary>
public static class SafeAlert
{
    public static async Task ShowAsync(string title, string message, string cancel = "OK")
    {
        try
        {
            await Shell.Current.DisplayAlert(title, message, cancel);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SafeAlert.ShowAsync failed to show '{title}': {ex.Message}");
        }
    }
}
