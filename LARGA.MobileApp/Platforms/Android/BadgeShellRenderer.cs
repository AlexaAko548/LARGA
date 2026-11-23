using System.Linq;
using Google.Android.Material.Badge;
using Google.Android.Material.BottomNavigation;
using LARGA.MobileApp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;
using AColor = Android.Graphics.Color;

namespace LARGA.MobileApp.Platforms.Android;

/// <summary>
/// MAUI 9 Shell has no tab badge API, so the manager tab bar's unread numbers (Chats, Alerts) are
/// drawn with Material's own BottomNavigationView badges: this renderer swaps in an appearance
/// tracker that puts them on. Registered for Shell in MauiProgram (Android only).
/// </summary>
internal sealed class BadgeShellRenderer : ShellRenderer
{
    protected override IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(ShellItem shellItem) =>
        new BadgeBottomNavAppearanceTracker(this, shellItem);
}

/// <summary>
/// The stock bottom-bar appearance plus number badges on the upper right of a tab's icon: the manager's
/// Chats (UnreadBadgeService.ChatsCount) and Alerts (AlertsCount) tabs, and the driver's Ledger
/// (LedgerCount) and Home (DriverMessagesCount) tabs, found by the tab's Route - not by position - so a
/// tab added or moved later still gets the right badge. Hidden at 0, "99+" above 99, in the theme's
/// danger color. Re-applied on every appearance update (the tab bar is rebuilt, the theme switches) and
/// whenever a count changes. A tab bar without one of those routes gets no badge for it.
/// </summary>
internal sealed class BadgeBottomNavAppearanceTracker : ShellBottomNavViewAppearanceTracker
{
    private static readonly (string Route, System.Func<UnreadBadgeService, int> Count)[] Badges =
    {
        ("chats", badges => badges.ChatsCount),
        ("alerts", badges => badges.AlertsCount),
        // The driver's Ledger tab: balance updates they haven't looked at (a shift they ended, a payment, a manager's
        // adjustment). The manager tab bar has no such route, so it gets none.
        ("ledger", badges => badges.LedgerCount),
        // The driver's Home tab carries the Message Manager button, so an unread manager message shows on the tab
        // from any other screen too. On the manager's bar "home" is the Map tab, whose count is always 0.
        ("home", badges => badges.DriverMessagesCount),
    };

    private readonly ShellItem _shellItem;
    private readonly UnreadBadgeService? _badges;
    private System.WeakReference<BottomNavigationView>? _bottomView;

    public BadgeBottomNavAppearanceTracker(IShellContext shellContext, ShellItem shellItem) : base(shellContext, shellItem)
    {
        _shellItem = shellItem;
        _badges = IPlatformApplication.Current?.Services.GetService<UnreadBadgeService>();
        if (_badges != null)
        {
            _badges.Changed += OnBadgesChanged;
        }
        if (Microsoft.Maui.Controls.Application.Current is { } app)
        {
            // ThemeService swaps the palette on the same event; this runs after it.
            app.RequestedThemeChanged += OnThemeChanged;
        }
    }

    public override void SetAppearance(BottomNavigationView bottomView, IShellAppearanceElement appearance)
    {
        base.SetAppearance(bottomView, appearance);
        Attach(bottomView);
    }

    public override void ResetAppearance(BottomNavigationView bottomView)
    {
        base.ResetAppearance(bottomView);
        Attach(bottomView);
    }

    private void Attach(BottomNavigationView bottomView)
    {
        _bottomView = new System.WeakReference<BottomNavigationView>(bottomView);
        Apply();
        // The menu can be rebuilt right after an appearance pass; apply again once it has settled.
        bottomView.Post(Apply);
    }

    private void OnBadgesChanged(object? sender, System.EventArgs e) => MainThread.BeginInvokeOnMainThread(Apply);

    private void OnThemeChanged(object? sender, AppThemeChangedEventArgs e) => MainThread.BeginInvokeOnMainThread(Apply);

    private void Apply()
    {
        if (_badges == null || _bottomView == null || !_bottomView.TryGetTarget(out BottomNavigationView? bottomView)
            || bottomView.Handle == System.IntPtr.Zero)
        {
            return;
        }

        try
        {
            foreach ((string route, System.Func<UnreadBadgeService, int> count) in Badges)
            {
                if (MenuItemIdFor(bottomView, route) is not int itemId)
                {
                    continue;
                }

                int number = count(_badges);
                if (number <= 0)
                {
                    bottomView.RemoveBadge(itemId);
                    continue;
                }

                BadgeDrawable badge = bottomView.GetOrCreateBadge(itemId);
                badge.Number = number;
                badge.MaxCharacterCount = 3; // "99+" above 99, same as ChatReadState.BadgeText
                badge.BackgroundColor = ThemeColor("LargaDanger", Colors.Red).ToArgb();
                badge.BadgeTextColor = ThemeColor("LargaWhite", Colors.White).ToArgb();
                badge.SetVisible(true);
            }
        }
        catch (System.Exception ex)
        {
            // A badge is never worth breaking the tab bar for.
            System.Diagnostics.Debug.WriteLine($"Tab badge update failed: {ex.Message}");
        }
    }

    // The menu item of the tab whose Route is this: the bottom bar shows one item per visible tab,
    // in order, titled like the tab.
    private int? MenuItemIdFor(BottomNavigationView bottomView, string route)
    {
        var tabs = ((IShellItemController)_shellItem).GetItems();
        ShellSection? tab = tabs.FirstOrDefault(s => s.Route == route);
        var menu = bottomView.Menu;
        if (tab == null || menu == null)
        {
            return null;
        }

        for (int i = 0; i < menu.Size(); i++)
        {
            var item = menu.GetItem(i);
            if (item?.TitleFormatted?.ToString() == tab.Title)
            {
                return item.ItemId;
            }
        }

        int index = tabs.IndexOf(tab);
        return index >= 0 && index < menu.Size() ? menu.GetItem(index)?.ItemId : null;
    }

    private static AColor ThemeColor(string key, Color fallback) =>
        (Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is Color color
            ? color
            : fallback).ToPlatform();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_badges != null)
            {
                _badges.Changed -= OnBadgesChanged;
            }
            if (Microsoft.Maui.Controls.Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnThemeChanged;
            }
        }
        base.Dispose(disposing);
    }
}
