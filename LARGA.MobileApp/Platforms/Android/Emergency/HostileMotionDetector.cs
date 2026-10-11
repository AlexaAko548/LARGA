using System;
using System.Collections.Generic;

namespace LARGA.MobileApp.Platforms.Android.Emergency;

/// <summary>
/// LAR-86 Hostile Protocol trigger: continuous, rhythmic X/Y-axis shaking for 3 seconds (the
/// driver shaking the phone side to side). Fed gravity-free accelerometer samples; no Android
/// types, so the thresholds can be tuned and tested on their own.
/// </summary>
public sealed class HostileMotionDetector
{
    private const double WindowSeconds = 3.0;
    // How much of the window must actually hold samples - sensors can stall briefly.
    private const double MinCoveredSeconds = 2.8;
    // RMS of the X/Y acceleration (m/s²). Normal driving vibration stays well under this.
    private const double MinRms = 3.0;
    // Rhythm: the shaking axis must swing back and forth 1.5-6 times a second...
    private const double MinFrequencyHz = 1.5;
    private const double MaxFrequencyHz = 6.0;
    // ...in every one-second slice of the window, so a single bump or pothole can't pass.
    private const int Slices = 3;
    private const double CooldownSeconds = 30.0;

    private readonly Queue<(double T, double X, double Y, double Z)> _window = new();
    private double _lastFiredAt = double.NegativeInfinity;

    /// <summary>Adds a sample (time in seconds); true the moment the pattern is recognized.</summary>
    public bool OnSample(double t, double x, double y, double z)
    {
        _window.Enqueue((t, x, y, z));
        while (_window.Count > 0 && t - _window.Peek().T > WindowSeconds)
        {
            _window.Dequeue();
        }

        if (t - _lastFiredAt < CooldownSeconds || t - _window.Peek().T < MinCoveredSeconds)
        {
            return false;
        }

        var samples = _window.ToArray();
        if (!IsRhythmicShake(samples))
        {
            return false;
        }

        _lastFiredAt = t;
        _window.Clear();
        return true;
    }

    public void Reset() => _window.Clear();

    private static bool IsRhythmicShake((double T, double X, double Y, double Z)[] samples)
    {
        double sumX = 0, sumY = 0, sumZ = 0;
        foreach (var s in samples)
        {
            sumX += s.X * s.X;
            sumY += s.Y * s.Y;
            sumZ += s.Z * s.Z;
        }

        double rmsXy = Math.Sqrt((sumX + sumY) / samples.Length);
        if (rmsXy < MinRms || sumZ >= sumX + sumY)
        {
            return false; // too weak, or mostly up-and-down (road bumps), not side to side
        }

        bool useX = sumX >= sumY;
        double axisRms = Math.Sqrt((useX ? sumX : sumY) / samples.Length);

        double start = samples[0].T;
        double sliceLength = (samples[^1].T - start) / Slices;
        for (int slice = 0; slice < Slices; slice++)
        {
            double from = start + slice * sliceLength;
            double to = from + sliceLength;
            double hz = CrossingsPerSecond(samples, useX, from, to, axisRms);
            if (hz < MinFrequencyHz || hz > MaxFrequencyHz)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Full back-and-forth swings per second on one axis, counted with hysteresis (the value has
    /// to clear ±half the RMS on the other side) so sensor noise around zero isn't counted.
    /// </summary>
    private static double CrossingsPerSecond(
        (double T, double X, double Y, double Z)[] samples, bool useX, double from, double to, double axisRms)
    {
        double band = axisRms * 0.5;
        int side = 0;
        int crossings = 0;
        foreach (var s in samples)
        {
            if (s.T < from || s.T > to) continue;

            double v = useX ? s.X : s.Y;
            int current = v > band ? 1 : v < -band ? -1 : 0;
            if (current == 0) continue;

            if (side != 0 && current != side) crossings++;
            side = current;
        }

        double seconds = to - from;
        return seconds <= 0 ? 0 : crossings / 2.0 / seconds;
    }
}
