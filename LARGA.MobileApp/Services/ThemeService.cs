using System;
using System.Linq;
using LARGA.MobileApp.Resources.Styles;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;

namespace LARGA.MobileApp.Services;

public enum AppThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>
/// App-wide System / Light / Dark setting, saved per device in Preferences.
///
/// Two halves: UserAppTheme drives the stock AppThemeBinding styles (default Label/Entry
/// colors, native controls, the status bar), and the app's own palette is swapped by replacing
/// LightColors with DarkColors in Application.Resources. Views reference palette keys with
/// DynamicResource, so they repaint in place without reloading pages.
/// </summary>
public interface IThemeService
{
    AppThemePreference Preference { get; }

    void SetPreference(AppThemePreference preference);

    /// <summary>Applies the saved preference. Called once at startup.</summary>
    void Initialize();
}

public class ThemeService : IThemeService
{
    private const string PreferenceKey = "app_theme";
    private bool _listening;

    public AppThemePreference Preference
    {
        get => Enum.TryParse(Preferences.Get(PreferenceKey, nameof(AppThemePreference.System)), out AppThemePreference saved)
            ? saved
            : AppThemePreference.System;
    }

    public void Initialize()
    {
        Application? app = Application.Current;
        if (app == null)
        {
            return;
        }

        if (!_listening)
        {
            // Fires when the OS theme flips (only matters on System) and when UserAppTheme is set.
            app.RequestedThemeChanged += (_, _) => MainThread.BeginInvokeOnMainThread(ApplyPalette);
            _listening = true;
        }

        Apply(app);
    }

    public void SetPreference(AppThemePreference preference)
    {
        Preferences.Set(PreferenceKey, preference.ToString());
        if (Application.Current != null)
        {
            Apply(Application.Current);
        }
    }

    private void Apply(Application app)
    {
        app.UserAppTheme = Preference switch
        {
            AppThemePreference.Light => AppTheme.Light,
            AppThemePreference.Dark => AppTheme.Dark,
            _ => AppTheme.Unspecified,
        };
        ApplyPalette();
    }

    private static void ApplyPalette()
    {
        Application? app = Application.Current;
        if (app == null)
        {
            return;
        }

        // RequestedTheme is UserAppTheme when set, otherwise the OS theme.
        bool dark = app.RequestedTheme == AppTheme.Dark;
        var merged = app.Resources.MergedDictionaries;
        ResourceDictionary? current = merged.FirstOrDefault(d => d is LightColors or DarkColors);

        if ((dark && current is DarkColors) || (!dark && current is LightColors))
        {
            return;
        }

        if (current != null)
        {
            merged.Remove(current);
        }
        merged.Add(dark ? new DarkColors() : new LightColors());
    }
}

/// <summary>Reads a palette color in code, for the few colors set from C#.</summary>
public static class ThemeColors
{
    public static Color Get(string key, Color fallback) =>
        Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is Color color
            ? color
            : fallback;
}
