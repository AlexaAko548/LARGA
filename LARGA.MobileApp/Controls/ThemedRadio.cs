using System.Linq;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Controls;

/// <summary>
/// Makes a plain (untemplated) RadioButton's circle readable in dark mode: white there, the
/// platform's own color in light mode. Usage:
///   controls:ThemedRadio.IsEnabled="True"
/// Like ThemedIcon, it follows Application.RequestedTheme, so it switches live with the palette.
/// </summary>
public static class ThemedRadio
{
    public static readonly BindableProperty IsEnabledProperty = BindableProperty.CreateAttached(
        "IsEnabled", typeof(bool), typeof(ThemedRadio), false, propertyChanged: OnIsEnabledChanged);

    public static bool GetIsEnabled(BindableObject view) => (bool)view.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(BindableObject view, bool value) => view.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not RadioButton radio)
        {
            return;
        }

        var existing = radio.Behaviors.OfType<ThemedRadioBehavior>().FirstOrDefault();
        if ((bool)newValue && existing == null)
        {
            radio.Behaviors.Add(new ThemedRadioBehavior());
        }
        else if (!(bool)newValue && existing != null)
        {
            radio.Behaviors.Remove(existing);
        }
    }

    private sealed class ThemedRadioBehavior : Behavior<RadioButton>
    {
        private RadioButton? _radio;
#if ANDROID
        private Android.Content.Res.ColorStateList? _lightTint;
        private bool _hasLightTint;
#endif

        protected override void OnAttachedTo(RadioButton radio)
        {
            base.OnAttachedTo(radio);
            _radio = radio;
            radio.HandlerChanged += OnHandlerChanged;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged += OnThemeChanged;
            }
            Apply();
        }

        protected override void OnDetachingFrom(RadioButton radio)
        {
            radio.HandlerChanged -= OnHandlerChanged;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnThemeChanged;
            }
            _radio = null;
            base.OnDetachingFrom(radio);
        }

        private void OnHandlerChanged(object? sender, System.EventArgs e)
        {
#if ANDROID
            _hasLightTint = false; // a new native view - remember its own tint again
#endif
            Apply();
        }

        private void OnThemeChanged(object? sender, AppThemeChangedEventArgs e) =>
            MainThread.BeginInvokeOnMainThread(Apply);

        private void Apply()
        {
#if ANDROID
            if (_radio?.Handler?.PlatformView is not Android.Widget.CompoundButton button)
            {
                return;
            }

            if (!_hasLightTint)
            {
                _lightTint = button.ButtonTintList;
                _hasLightTint = true;
            }

            bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
            button.ButtonTintList = dark
                ? Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.White)
                : _lightTint;
#endif
        }
    }
}
