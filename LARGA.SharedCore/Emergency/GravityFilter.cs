namespace LARGA.SharedCore.Emergency;

/// <summary>
/// Turns raw accelerometer readings (which include the constant 1G pull) into gravity-free
/// acceleration for <see cref="HostileMotionDetector"/> and <see cref="CrashDetector"/>: a
/// low-pass filter isolates the gravity component, which is then subtracted. The first reading
/// primes the filter (so it reads as no movement).
/// </summary>
public sealed class GravityFilter
{
    private const float GravityAlpha = 0.8f;

    private readonly float[] _gravity = new float[3];
    private bool _primed;

    /// <summary>Raw accelerometer axes (m/s²) in; gravity-free axes (m/s²) out.</summary>
    public (double X, double Y, double Z) Linear(float rawX, float rawY, float rawZ)
    {
        if (!_primed)
        {
            _gravity[0] = rawX;
            _gravity[1] = rawY;
            _gravity[2] = rawZ;
            _primed = true;
        }
        else
        {
            _gravity[0] = GravityAlpha * _gravity[0] + (1 - GravityAlpha) * rawX;
            _gravity[1] = GravityAlpha * _gravity[1] + (1 - GravityAlpha) * rawY;
            _gravity[2] = GravityAlpha * _gravity[2] + (1 - GravityAlpha) * rawZ;
        }

        return (rawX - _gravity[0], rawY - _gravity[1], rawZ - _gravity[2]);
    }
}
