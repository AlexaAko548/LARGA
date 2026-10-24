using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace LARGA.SharedCore.Emergency;

/// <summary>
/// A recorded (or hand-made) run of accelerometer readings and GPS speeds, for replaying through
/// the same detectors the phone uses. One reading per line, times in seconds from the start:
/// <code>
/// # comment
/// a,&lt;t&gt;,&lt;x&gt;,&lt;y&gt;,&lt;z&gt;    raw accelerometer, m/s² (gravity included, as the sensor reports it)
/// g,&lt;t&gt;,&lt;speed&gt;[,provider]   GPS fix speed in m/s; empty for a fix without speed (provider is ignored)
/// </code>
/// The DEBUG build records this format (EmergencyDetectionService RECORD) and replays it (REPLAY);
/// LARGA.Tests replays the same files from Fixtures.
/// </summary>
public sealed class SensorTrace
{
    public readonly record struct Sample(double T, bool IsGps, float X, float Y, float Z, double? Speed);

    public IReadOnlyList<Sample> Samples { get; }

    private SensorTrace(IReadOnlyList<Sample> samples) => Samples = samples;

    public static SensorTrace Parse(string text)
    {
        var samples = new List<Sample>();
        using var reader = new StringReader(text);
        int lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            string[] parts = line.Split(',');
            try
            {
                double t = Num(parts[1]);
                switch (parts[0].Trim())
                {
                    case "a":
                        samples.Add(new Sample(t, false, (float)Num(parts[2]), (float)Num(parts[3]), (float)Num(parts[4]), null));
                        break;
                    case "g":
                        string speed = parts.Length > 2 ? parts[2].Trim() : string.Empty;
                        samples.Add(new Sample(t, true, 0, 0, 0, speed.Length == 0 ? null : Num(speed)));
                        break;
                    default:
                        throw new FormatException($"unknown record '{parts[0]}'");
                }
            }
            catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException)
            {
                throw new FormatException($"Sensor trace line {lineNumber}: {ex.Message}", ex);
            }
        }

        return new SensorTrace(samples.OrderBy(s => s.T).ToList());
    }

    public static string FormatAccel(double t, float x, float y, float z) =>
        string.Create(CultureInfo.InvariantCulture, $"a,{t:0.000},{x:0.###},{y:0.###},{z:0.###}");

    public static string FormatGps(double t, double? speed) =>
        string.Create(CultureInfo.InvariantCulture, $"g,{t:0.000},{(speed is double s ? s.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty)}");

    private static double Num(string s) => double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
}

/// <summary>
/// Feeds a <see cref="SensorTrace"/> through the phone's detection pipeline (gravity filter, then
/// <see cref="CrashDetector"/> and <see cref="HostileMotionDetector"/>, in the same order as
/// EmergencyDetectionService) and reports what fired and when.
/// </summary>
public static class EmergencyTraceReplayer
{
    public readonly record struct Detection(double T, string TriggerType);

    /// <summary>Replays into fresh detectors. <paramref name="startAt"/> shifts the trace's clock.</summary>
    public static List<Detection> Run(SensorTrace trace, double startAt = 0) =>
        Run(trace, new GravityFilter(), new HostileMotionDetector(), new CrashDetector(), startAt);

    public static List<Detection> Run(SensorTrace trace, GravityFilter gravity, HostileMotionDetector hostile, CrashDetector crash, double startAt = 0)
    {
        var detections = new List<Detection>();
        foreach (SensorTrace.Sample s in trace.Samples)
        {
            double t = startAt + s.T;
            if (s.IsGps)
            {
                if (crash.OnSpeed(t, s.Speed))
                {
                    detections.Add(new Detection(t, Shared.Models.Entities.EmergencyAlert.Crash));
                }
                continue;
            }

            (double x, double y, double z) = gravity.Linear(s.X, s.Y, s.Z);
            crash.OnLinearAcceleration(t, x, y, z);
            if (hostile.OnSample(t, x, y, z))
            {
                detections.Add(new Detection(t, Shared.Models.Entities.EmergencyAlert.Hostile));
            }
        }
        return detections;
    }
}
