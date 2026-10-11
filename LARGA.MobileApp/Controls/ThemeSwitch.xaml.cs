using System;
using LARGA.MobileApp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Controls;

public partial class ThemeSwitch : ContentView
{
    private readonly IThemeService? _themeService;

    public ThemeSwitch()
    {
        InitializeComponent();

        // Resolved here rather than through each profile page's ViewModel, so the control can be
        // dropped onto any page as-is.
        _themeService = IPlatformApplication.Current?.Services.GetService<IThemeService>();

        foreach (Button button in new[] { SystemButton, LightButton, DarkButton })
        {
            button.FontSize = 12;
            button.Padding = new Thickness(12, 0);
            button.HeightRequest = 32;
            button.MinimumHeightRequest = 32;
            button.CornerRadius = 16;
            button.BorderWidth = 0;
        }

        Refresh();
    }

    private void OnSystemClicked(object? sender, EventArgs e) => Select(AppThemePreference.System);
    private void OnLightClicked(object? sender, EventArgs e) => Select(AppThemePreference.Light);
    private void OnDarkClicked(object? sender, EventArgs e) => Select(AppThemePreference.Dark);

    private void Select(AppThemePreference preference)
    {
        _themeService?.SetPreference(preference);
        Refresh();
    }

    private void Refresh()
    {
        AppThemePreference current = _themeService?.Preference ?? AppThemePreference.System;
        Paint(SystemButton, current == AppThemePreference.System);
        Paint(LightButton, current == AppThemePreference.Light);
        Paint(DarkButton, current == AppThemePreference.Dark);
    }

    private static void Paint(Button button, bool selected)
    {
        if (selected)
        {
            button.SetDynamicResource(Button.BackgroundColorProperty, "LargaPrimaryBlue");
            button.SetDynamicResource(Button.TextColorProperty, "LargaWhite");
        }
        else
        {
            button.BackgroundColor = Microsoft.Maui.Graphics.Colors.Transparent;
            button.SetDynamicResource(Button.TextColorProperty, "LargaDarkText");
        }
    }
}
