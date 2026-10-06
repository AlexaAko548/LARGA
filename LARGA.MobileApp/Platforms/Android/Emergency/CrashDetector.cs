using System;

namespace LARGA.MobileApp.Platforms.Android.Emergency;

/// <summary>
/// LAR-87 Accident Protocol trigger: a Z-axis G-force spike above 4G immediately followed by
/// the taxi's GPS speed dropping to zero. All times are seconds on the same clock
/// (SystemClock.elapsedRealtime, which both sensor events and GPS fixes carry).
/// </summary>
public sealed class CrashDetector
{
    private const double StandardGravity = 9.80665;
    private const double SpikeThreshold = 4 * StandardGravity;
    // How long after the spike the taxi has to come to a stop for it to count as a crash.
    private const double ConfirmWindowSeconds = 10.0;
    private const double StoppedSpeed = 0.5;           // m/s (~2 km/h - GPS jitter at rest)
    private const int RequiredStoppedFixes = 2;
    // The taxi must have been moving just before the spike: a phone dropped on the floor at a
    // red light is a 4G spike followed by zero speed too.
    private const double MovingSpeed = 2.8;             // m/s (~10 km/h)
    private const double MovingLookbackSeconds = 15.0;
    private const double CooldownSeconds = 60.0;

    private double _lastMovingAt = double.NegativeInfinity;
    private double? _spikeAt;
    private int _stoppedFixes;
    private double _lastFiredAt = double.NegativeInfinity;

    /// <summary>Gravity-free Z-axis acceleration, m/s².</summary>
    public void OnAcceleration(double t, double z)
    {
        if (_spikeAt is double spike && t - spike > ConfirmWindowSeconds)
        {
            _spikeAt = null; // the taxi kept going - not a crash
        }

        if (_spikeAt is null
            && Math.Abs(z) > SpikeThreshold
            && t - _lastFiredAt > CooldownSeconds
            && t - _lastMovingAt <= MovingLookbackSeconds)
        {
            _spikeAt = t;
            _stoppedFixes = 0;
        }
    }

    /// <summary>A GPS fix's speed (m/s), or null when the fix has none. True when it confirms a crash.</summary>
    public bool OnSpeed(double t, double? speed)
    {
        if (speed is null) return false;

        if (speed >= MovingSpeed)
        {
            _lastMovingAt = t;
        }

        if (_spikeAt is not double spike || t < spike)
        {
            return false;
        }

        if (t - spike > ConfirmWindowSeconds)
        {
            _spikeAt = null;
            return false;
        }

        if (speed >= StoppedSpeed)
        {
            _stoppedFixes = 0;
            return false;
        }

        if (++_stoppedFixes < RequiredStoppedFixes)
        {
            return false;
        }

        _spikeAt = null;
        _lastFiredAt = t;
        return true;
    }
}
