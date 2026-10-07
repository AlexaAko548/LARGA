using System.Linq;
using CommunityToolkit.Maui.Behaviors;
using LARGA.MobileApp.Resources.Styles;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace LARGA.MobileApp.Controls;

/// <summary>
/// Tints a black-glyph icon (back/check/close/phone/send/car/warning…) with the theme's text
/// color: black in light mode, light in dark mode. Usage on an Image or ImageButton:
///   controls:ThemedIcon.IsEnabled="True"
/// The tint follows Application.RequestedTheme (what ThemeService sets via UserAppTheme), so it
/// switches live along with the palette.
/// </summary>
public static class ThemedIcon
{
    // Read from the palettes so the icon tint can't drift from the text color.
    private static readonly Color LightTint = (Color)new LightColors()["ThemeTextBlack"];
    private static readonly Color DarkTint = (Color)new DarkColors()["ThemeTextBlack"];

    public static readonly BindableProperty IsEnabledProperty = BindableProperty.CreateAttached(
        "IsEnabled", typeof(bool), typeof(ThemedIcon), false, propertyChanged: OnIsEnabledChanged);

    public static bool GetIsEnabled(BindableObject view) => (bool)view.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(BindableObject view, bool value) => view.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not View view)
        {
            return;
        }

        var existing = view.Behaviors.OfType<ThemedIconTintBehavior>().FirstOrDefault();
        if ((bool)newValue && existing == null)
        {
            var behavior = new ThemedIconTintBehavior();
            behavior.SetAppThemeColor(IconTintColorBehavior.TintColorProperty, LightTint, DarkTint);
            view.Behaviors.Add(behavior);
        }
        else if (!(bool)newValue && existing != null)
        {
            view.Behaviors.Remove(existing);
        }
    }

    // Own type so it can be told apart from a page's explicit IconTintColorBehavior.
    private sealed class ThemedIconTintBehavior : IconTintColorBehavior
    {
    }
}
