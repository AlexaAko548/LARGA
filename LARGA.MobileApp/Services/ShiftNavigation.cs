using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Services;

public static class ShiftNavigation
{
    /// <summary>
    /// Opens the Active Shift screen once a shift has started, and drops the clock-in pages
    /// behind it (pre-shift steps, clock-in pending), so Back from Active Shift goes to the
    /// driver dashboard instead of returning into an inspection that's already submitted.
    /// Removing them after the push (rather than popping to the dashboard first) avoids
    /// flashing the dashboard on the way.
    /// </summary>
    public static async Task GoToActiveShiftAsync()
    {
        Shell? shell = Shell.Current;
        if (shell == null)
        {
            return;
        }

        await shell.GoToAsync("active-shift");

        var stack = shell.Navigation.NavigationStack;
        // [0] is the tab's root (the dashboard, null in Shell's stack), [^1] the new Active Shift.
        foreach (Page page in stack.Skip(1).Take(stack.Count - 2).Where(p => p != null).ToList())
        {
            shell.Navigation.RemovePage(page);
        }
    }
}
