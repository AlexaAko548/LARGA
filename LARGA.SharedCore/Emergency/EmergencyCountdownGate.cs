using System.Threading;

namespace LARGA.SharedCore.Emergency;

/// <summary>
/// "Only one emergency at a time" for the LAR-86/87 cancel countdown. The detection service asks
/// <see cref="TryBegin"/> on every detection; a second one while a countdown runs is ignored.
/// Cancel and expiry race each other (the popup, the notification action and the timer run on
/// different threads), so exactly one of <see cref="Cancel"/> and <see cref="TryComplete"/> wins.
/// </summary>
public sealed class EmergencyCountdownGate
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private string? _triggerType;

    /// <summary>The trigger type of the running countdown, or null.</summary>
    public string? Active
    {
        get { lock (_lock) return _triggerType; }
    }

    /// <summary>Starts a countdown. False (and nothing changes) when one is already running.</summary>
    public bool TryBegin(string triggerType, out CancellationToken token)
    {
        lock (_lock)
        {
            if (_cts != null)
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
            _cts = null;
            _triggerType = null;
        }

        if (cts == null) return false;

        cts.Cancel();
        return true;
    }

    /// <summary>Ends a countdown that ran out. False if it was cancelled in the meantime.</summary>
    public bool TryComplete()
    {
        lock (_lock)
        {
            if (_cts == null) return false;
            _cts = null;
            _triggerType = null;
            return true;
        }
    }
}
