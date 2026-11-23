using Android.App;
using Android.Content;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using LARGA.MobileApp.Controls;
using Microsoft.Maui.Controls;
using AView = Android.Views.View;

namespace LARGA.MobileApp.Platforms.Android;

/// <summary>
/// Tap anywhere to hide the keyboard, for every page (MainActivity.DispatchTouchEvent): when a text
/// field (Entry, Editor, SearchBar) has focus and the user taps outside it, the soft keyboard is
/// hidden and the field loses focus. The touch is always passed on first, so the tapped button
/// still works. Not hidden for:
///  - a drag or scroll (moved more than the touch slop) - scrolling the chat keeps the keyboard;
///  - a long press (the SOS hold ring) - only quick taps count;
///  - a tap into a text field, the focused one or another (focus moves there, keyboard stays);
///  - a control marked KeyboardDismiss.KeepsKeyboardOpen (the chat Send buttons).
/// Popups (CommunityToolkit, ConfirmationPopup, OptionPickerPopup) are dialogs with their own
/// window, so their touches never come through here.
/// </summary>
internal sealed class KeyboardDismisser
{
    private float _downX;
    private float _downY;
    private long _downTime;
    private bool _tracking;

    /// <summary>Call from Activity.DispatchTouchEvent with the base implementation.</summary>
    public bool Dispatch(Activity activity, MotionEvent e, System.Func<MotionEvent, bool> baseDispatch)
    {
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _tracking = activity.CurrentFocus is EditText;
                _downX = e.RawX;
                _downY = e.RawY;
                _downTime = e.EventTime;
                break;
            case MotionEventActions.Move:
                int slop = ViewConfiguration.Get(activity)?.ScaledTouchSlop ?? 16;
                if (System.Math.Abs(e.RawX - _downX) > slop || System.Math.Abs(e.RawY - _downY) > slop)
                {
                    _tracking = false;
                }
                break;
            case MotionEventActions.PointerDown:
            case MotionEventActions.Cancel:
                _tracking = false;
                break;
        }

        // The tap reaches its target first: a text field it lands on takes focus on this same UP.
        bool handled = baseDispatch(e);

        if (e.ActionMasked == MotionEventActions.Up && _tracking)
        {
            _tracking = false;
            if (e.EventTime - _downTime <= ViewConfiguration.LongPressTimeout)
            {
                HideIfOutsideTextField(activity, e.RawX, e.RawY);
            }
        }
        return handled;
    }

    private static void HideIfOutsideTextField(Activity activity, float x, float y)
    {
        AView? focused = activity.CurrentFocus;
        if (focused is EditText && Contains(InputArea(focused), x, y))
        {
            return; // in the field: place the cursor, keep typing
        }

        foreach (VisualElement element in KeyboardDismiss.KeepOpenElements())
        {
            if (element.IsVisible && element.Handler?.PlatformView is AView view && view.IsShown && Contains(view, x, y))
            {
                return;
            }
        }

        var input = (InputMethodManager?)activity.GetSystemService(Context.InputMethodService);
        input?.HideSoftInputFromWindow((focused ?? activity.Window?.DecorView)?.WindowToken, HideSoftInputFlags.None);
        if (focused is EditText)
        {
            focused.ClearFocus();
        }
    }

    // A SearchBar's text box sits inside a SearchView with its own icons; tapping those is still "in" it.
    private static AView InputArea(AView focused)
    {
        for (IViewParent? parent = focused.Parent; parent is AView view; parent = view.Parent)
        {
            if (view is SearchView)
            {
                return view;
            }
        }
        return focused;
    }

    private static bool Contains(AView view, float x, float y)
    {
        if (!view.IsAttachedToWindow)
        {
            return false;
        }

        int[] location = new int[2];
        view.GetLocationOnScreen(location);
        var bounds = new global::Android.Graphics.Rect(location[0], location[1], location[0] + view.Width, location[1] + view.Height);
        return bounds.Contains((int)x, (int)y);
    }
}
