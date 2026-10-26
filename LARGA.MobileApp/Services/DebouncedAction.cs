using System;
using System.Threading;
using System.Threading.Tasks;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Coalesces rapid-fire calls - several Firestore listener events landing together (LAR: driver Ledger/Quick
/// Ledger auto-refresh) - into one run of <paramref name="action"/> after a quiet period, instead of reloading
/// once per event.
/// </summary>
public sealed class DebouncedAction : IDisposable
{
    private readonly Func<Task> _action;
    private readonly TimeSpan _delay;
    private CancellationTokenSource? _cts;

    public DebouncedAction(Func<Task> action, TimeSpan delay)
    {
        _action = action;
        _delay = delay;
    }

    public void Trigger()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = RunAsync(cts.Token);
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_delay, token);
            if (!token.IsCancellationRequested)
            {
                await _action();
            }
        }
        catch (TaskCanceledException)
        {
            // Superseded by a later Trigger().
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
