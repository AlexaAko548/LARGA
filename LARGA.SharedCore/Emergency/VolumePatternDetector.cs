using System.Collections.Generic;

namespace LARGA.SharedCore.Emergency;

/// <summary>
/// Hostile Protocol trigger from the volume buttons: down, up, down, up within 3 seconds. To
/// anyone watching it looks like the driver fiddling with the volume (and the volume ends where it
/// started), but it almost never happens by accident. Fed key-down presses only (no auto-repeat);
/// no Android types, so it can be tested on its own.
/// </summary>
public sealed class VolumePatternDetector
{
    public const double WindowSeconds = 3.0;

    // false = volume down, true = volume up.
    private static readonly bool[] Pattern = { false, true, false, true };

    private readonly Queue<(double T, bool Up)> _presses = new();

    /// <summary>Adds a press (time in seconds); true the moment the pattern completes.</summary>
    public bool OnPress(double t, bool up)
    {
        _presses.Enqueue((t, up));
        while (_presses.Count > Pattern.Length)
        {
            _presses.Dequeue();
        }

        if (_presses.Count < Pattern.Length)
        {
            return false;
        }

        (double T, bool Up)[] presses = _presses.ToArray();
        if (presses[^1].T - presses[0].T > WindowSeconds)
        {
            return false;
        }

        for (int i = 0; i < Pattern.Length; i++)
        {
            if (presses[i].Up != Pattern[i]) return false;
        }

        // A full new pattern is needed for the next match (the repeat that cancels the countdown).
        _presses.Clear();
        return true;
    }

    public void Reset() => _presses.Clear();
}
