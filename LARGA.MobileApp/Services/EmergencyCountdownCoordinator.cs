using System;

namespace LARGA.MobileApp.Services;

/// <summary>
/// The 5-second "cancel before it's sent" window of an automated SOS (LAR-86/87), shared between
/// the Android detection service that runs it and the popup that shows it. The service calls
/// Begin/End; the popup reads Active and calls RequestCancel.
/// </summary>
public class EmergencyCountdownCoordinator
{
    private readonly object _gate = new();
    private (string TriggerType, DateTime EndsAtUtc)? _active;

    /// <summary>The running countdown (EmergencyAlert.Hostile or Crash), or null when none is.</summary>
    public (string TriggerType, DateTime EndsAtUtc)? Active
    {
        get { lock (_gate) return _active; }
    }

    /// <summary>Raised (on any thread) when a countdown starts or ends.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the driver presses Cancel on the popup.</summary>
    public event EventHandler? CancelRequested;

    public void Begin(string triggerType, TimeSpan duration)
    {
        lock (_gate) _active = (triggerType, DateTime.UtcNow + duration);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void End()
    {
        lock (_gate)
        {
            if (_active is null) return;
            _active = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RequestCancel()
    {
        if (Active is null) return;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }
}
