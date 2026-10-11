using System;

namespace LARGA.MobileApp.Platforms.Android.Emergency;

/// <summary>
/// LAR-87 Accident Protocol trigger: a G-force spike above 4G immediately followed by the taxi's
/// GPS speed dropping to zero. The spike is the magnitude of the gravity-free acceleration on all
/// three axes, not the Z axis alone: which phone axis a frontal or side impact lands on depends on
/// how the phone is mounted (upright on the dash, flat in a tray, in a pocket), and a Z-only check
/// missed impacts along the others. A Z-axis spike still counts, as before. All times are seconds
/// on the same clock (SystemClock.elapsedRealtime, which both sensor events and GPS fixes carry).
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

    /// <summary>Gravity-free acceleration on each axis, m/s².</summary>
    public void OnLinearAcceleration(double t, double x, double y, double z)
    {
        if (_spikeAt is double spike && t - spike > ConfirmWindowSeconds)
        {
            _spikeAt = null; // the taxi kept going - not a crash
        }

        double magnitude = Math.Sqrt(x * x + y * y + z * z);
        if (_spikeAt is null
            && magnitude > SpikeThreshold
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
