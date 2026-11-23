namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Whether the global SOS banner (Components/Shared/SosBanner.razor) has been dismissed, shared
/// per browser session so closing it on one page keeps it closed on the next - each page tears
/// down and recreates SosBanner on navigation (there's no persistent layout), so this can't live
/// on the component itself. Any change in the active-alert count since the dismissal clears it.
/// </summary>
public class SosBannerState
{
    public bool Dismissed { get; private set; }
    private int _dismissedAtCount;

    public void Dismiss(int atCount)
    {
        Dismissed = true;
        _dismissedAtCount = atCount;
    }

    /// <summary>Called whenever the banner (re)loads the active count.</summary>
    public void ReconcileCount(int currentCount)
    {
        if (Dismissed && currentCount != _dismissedAtCount)
        {
            Dismissed = false;
        }
    }
}
