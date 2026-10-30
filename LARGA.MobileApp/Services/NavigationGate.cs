using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Services;

/// <summary>
/// One tap-driven navigation at a time, app-wide. Tapping "+ Add Fuel Report" (or any button that opens
/// a screen) several times before the first screen is up would otherwise push a copy per tap; a back
/// arrow tapped twice would pop two screens. While a navigation started here is still running, the next
/// one is ignored. Use it for navigations a user's tap starts - not for the app's own redirects (sign-in
/// routing, SOS countdown, shift-state syncing), which must never be dropped.
/// </summary>
public static class NavigationGate
{
    private static readonly BusyGate Gate = new();

    public static bool IsBusy => Gate.IsBusy;

    /// <summary>Runs <paramref name="navigate"/> unless another gated navigation is still in progress.</summary>
    public static Task RunAsync(Func<Task> navigate) => Gate.RunAsync(navigate, showLoading: false);

    public static Task GoToAsync(string route) => RunAsync(() => Shell.Current.GoToAsync(route));

    public static Task GoToAsync(string route, IDictionary<string, object> parameters) =>
        RunAsync(() => Shell.Current.GoToAsync(route, parameters));

    public static Task GoBackAsync() => GoToAsync("..");

    /// <summary>The page is only created once the tap is accepted, so an ignored tap doesn't build
    /// (and start the camera of) a page that is never shown.</summary>
    public static Task PushModalAsync(Func<Page> createPage) =>
        RunAsync(() => Shell.Current.Navigation.PushModalAsync(createPage()));
}
