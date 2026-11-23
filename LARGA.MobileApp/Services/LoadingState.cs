using System;
using System.Threading;

namespace LARGA.MobileApp.Services;

/// <summary>
/// LAR-117: app-wide "something is loading from Firestore" state. A screen wraps its load in
/// <c>using var _ = LoadingState.Begin();</c>; while any load is running, LoadingWindowOverlay
/// shows its indicator over whatever page is open - so no screen looks stuck, and screens don't
/// each need their own spinner layout. Counted, so overlapping loads keep it up until the last ends.
/// </summary>
public static class LoadingState
{
    private static int _active;

    /// <summary>Raised (on any thread) when IsBusy flips.</summary>
    public static event EventHandler? Changed;

    public static bool IsBusy => Volatile.Read(ref _active) > 0;

    public static IDisposable Begin()
    {
        if (Interlocked.Increment(ref _active) == 1)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && Interlocked.Decrement(ref _active) == 0)
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }
    }
}
