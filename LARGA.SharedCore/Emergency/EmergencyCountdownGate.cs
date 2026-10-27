using System.Threading;

namespace LARGA.SharedCore.Emergency;

/// <summary>
/// "Only one emergency at a time" for the LAR-86/87 cancel countdown. The detection service asks
/// <see cref="TryBegin"/> on every detection; a second one while a countdown runs, or while the
/// alert it led to is still being written (<see cref="TryComplete"/> until <see cref="Finish"/>),
/// is ignored. Cancel and expiry race each other (the popup, the notification action and the timer
/// run on different threads), so exactly one of <see cref="Cancel"/> and <see cref="TryComplete"/> wins.
/// </summary>
public sealed class EmergencyCountdownGate
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private string? _triggerType;
    private bool _sending;

    /// <summary>True while the alert of a countdown that ran out is being written.</summary>
    public bool Sending
    {
        get { lock (_lock) return _sending; }
    }

    /// <summary>The trigger type of the running countdown (or of the alert being written), or null.</summary>
    public string? Active
    {
        get { lock (_lock) return _triggerType; }
    }

    /// <summary>Starts a countdown. False (and nothing changes) when one is already running.</summary>
    public bool TryBegin(string triggerType, out CancellationToken token)
    {
        lock (_lock)
        {
            if (_cts != null || _sending)
            {
                token = default;
                return false;
            }

            _cts = new CancellationTokenSource();
            _triggerType = triggerType;
            token = _cts.Token;
            return true;
        }
    }

    /// <summary>Cancels the running countdown. False when there was none (already sent or cancelled).</summary>
    public bool Cancel()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            cts = _cts;
            if (cts == null) return false; // nothing counting down - an alert being written can't be cancelled
            _cts = null;
            _triggerType = null;
        }

        cts.Cancel();
        return true;
    }

    /// <summary>Ends a countdown that ran out and holds the gate while its alert is written. False if it
    /// was cancelled in the meantime. Call <see cref="Finish"/> once the alert is written (or failed).</summary>
    public bool TryComplete()
    {
        lock (_lock)
        {
            if (_cts == null) return false;
            _cts = null;
            _sending = true;
            return true;
        }
    }

    /// <summary>The alert is written (or failed): the next detection may start a countdown.</summary>
    public void Finish()
    {
        lock (_lock)
        {
            _sending = false;
            _triggerType = null;
        }
    }
}
