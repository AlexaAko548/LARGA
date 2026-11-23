using System;
using System.Threading.Tasks;

namespace LARGA.MobileApp.Services;

/// <summary>
/// One run at a time for a button's action, with the app-wide loading indicator (LoadingState) while it runs.
/// A second tap while the first is still saving is ignored, so nothing is submitted twice. Screens bind their
/// button's IsEnabled to <see cref="IsBusy"/> (inverted) through <see cref="Changed"/>.
/// </summary>
public sealed class BusyGate
{
    private bool _isBusy;

    public bool IsBusy => _isBusy;

    /// <summary>Raised (on the caller's thread) when <see cref="IsBusy"/> flips.</summary>
    public event EventHandler? Changed;

    /// <summary>Runs <paramref name="work"/> unless a run is already in progress. <paramref name="showLoading"/>
    /// false for actions that wait on the user (prompts, confirmations) - they show the indicator around their own
    /// writes instead.</summary>
    public async Task RunAsync(Func<Task> work, bool showLoading = true)
    {
        if (_isBusy) return;

        SetBusy(true);
        using IDisposable? loading = showLoading ? LoadingState.Begin() : null;
        try
        {
            await work();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        _isBusy = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
