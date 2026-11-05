using System;
using System.Collections.Generic;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Controls;

/// <summary>
/// Tapping anywhere outside the focused text field hides the soft keyboard (Android:
/// MainActivity.DispatchTouchEvent → KeyboardDismisser) - some phones have no Back button to close
/// it. A control that must leave the keyboard up when tapped opts out with
///   controls:KeyboardDismiss.KeepsKeyboardOpen="True"
/// e.g. the chat Send buttons, so the driver or manager can keep typing after sending.
/// </summary>
public static class KeyboardDismiss
{
    public static readonly BindableProperty KeepsKeyboardOpenProperty = BindableProperty.CreateAttached(
        "KeepsKeyboardOpen", typeof(bool), typeof(KeyboardDismiss), false, propertyChanged: OnKeepsKeyboardOpenChanged);

    // Weak, so a page that's gone isn't kept alive by its Send button being listed here.
    private static readonly List<WeakReference<VisualElement>> s_keepOpen = new();

    public static bool GetKeepsKeyboardOpen(BindableObject view) => (bool)view.GetValue(KeepsKeyboardOpenProperty);

    public static void SetKeepsKeyboardOpen(BindableObject view, bool value) => view.SetValue(KeepsKeyboardOpenProperty, value);

    /// <summary>The live controls that keep the keyboard open (main thread).</summary>
    public static IReadOnlyList<VisualElement> KeepOpenElements()
    {
        var alive = new List<VisualElement>();
        s_keepOpen.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (WeakReference<VisualElement> reference in s_keepOpen)
        {
            if (reference.TryGetTarget(out VisualElement? element) && GetKeepsKeyboardOpen(element))
            {
                alive.Add(element);
            }
        }
        return alive;
    }

    private static void OnKeepsKeyboardOpenChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is VisualElement element && (bool)newValue)
        {
            s_keepOpen.Add(new WeakReference<VisualElement>(element));
        }
    }
}
