using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Emergency;

namespace LARGA.Tests;

/// <summary>
/// M7 SOS Emergency - the Hostile shake and Crash detectors, fed the sensor traces in Fixtures
/// through the same pipeline as EmergencyDetectionService (gravity filter, then both detectors).
/// </summary>
public class SosDetectionTests
{
    private const double G = 9.80665;

    // ---- Hostile ------------------------------------------------------------

    [Fact]
    public void SOS_008_side_to_side_shake_for_3_seconds_triggers_hostile()
    {
        var detections = SosTraces.Replay("sos008_hostile_shake_3hz.txt");

        var hostile = Assert.Single(detections);
        Assert.Equal(EmergencyAlert.Hostile, hostile.TriggerType);
    }

    [Theory]
    [InlineData("sos010_vertical_shake.txt")]
    [InlineData("sos010_rough_road_120s.txt")]
    public void SOS_010_short_vertical_or_road_shaking_does_not_trigger(string trace)
    {
        Assert.Empty(SosTraces.Replay(trace));
    }

    [Theory]
    [InlineData("sos011_slow_sway_1hz.txt")]
    [InlineData("sos011_fast_vibration_8hz.txt")]
    public void SOS_011_rhythm_outside_1_5_to_6_swings_per_second_does_not_trigger(string trace)
    {
        Assert.Empty(SosTraces.Replay(trace));
    }

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(2.0, true)]
    [InlineData(3.0, true)]
    [InlineData(5.0, true)]
    [InlineData(8.0, false)]
    public void SOS_011_swing_rate_limits(double hz, bool triggers)
    {
        var detector = new HostileMotionDetector();
        bool fired = false;
        for (int i = 0; i <= 50 * 4; i++)
        {
            double t = i / 50.0;
            fired |= detector.OnSample(t, 8 * Math.Sin(2 * Math.PI * hz * t), 0, 0);
        }
        Assert.Equal(triggers, fired);
    }

    [Fact]
    public void SOS_012_second_shake_within_30_seconds_is_ignored_until_the_cooldown_passes()
    {
        var detector = new HostileMotionDetector();
        var fired = new List<double>();
        // Three 4-second shakes, starting at 0 s, 20 s and 40 s.
        foreach (double start in new[] { 0.0, 20.0, 40.0 })
        {
            for (int i = 0; i <= 50 * 4; i++)
            {
                double t = start + i / 50.0;
                if (detector.OnSample(t, 8 * Math.Sin(2 * Math.PI * 3 * t), 0, 0)) fired.Add(t);
            }
        }

        Assert.Equal(2, fired.Count);
        Assert.InRange(fired[0], 2.8, 4.0);    // first shake
        Assert.InRange(fired[1], 40.0, 44.0);  // the one at 20 s was inside the 30 s cooldown
        Assert.True(fired[1] - fired[0] >= 30);
    }

    // ---- Crash --------------------------------------------------------------

    [Fact]
    public void SOS_018_spike_above_4G_then_two_stopped_fixes_within_10s_is_a_crash()
    {
        var detection = Assert.Single(SosTraces.Replay("sos018_crash_spike_then_stop.txt"));

        Assert.Equal(EmergencyAlert.Crash, detection.TriggerType);
        Assert.Equal(22.5, detection.T, 3); // second fix below 2 km/h after the 20 s spike
    }

    [Fact]
    public void SOS_019_spike_while_the_car_keeps_moving_is_not_a_crash()
    {
        Assert.Empty(SosTraces.Replay("sos019_pothole_keeps_moving.txt"));
    }

    [Fact]
    public void SOS_020_phone_dropped_while_parked_is_not_a_crash()
    {
        Assert.Empty(SosTraces.Replay("sos020_drop_while_parked.txt"));
    }

    [Fact]
    public void SOS_021_threshold_3_9G_does_not_trigger_and_4_1G_does()
    {
        Assert.Empty(SosTraces.Replay("sos021_3_9g_then_stop.txt"));
        Assert.Equal(EmergencyAlert.Crash, Assert.Single(SosTraces.Replay("sos021_4_1g_then_stop.txt")).TriggerType);
    }

    [Theory]
    [InlineData(3.9, false)]
    [InlineData(3.99, false)]
    [InlineData(4.01, true)]
    [InlineData(4.1, true)]
    public void SOS_021_threshold_on_the_gravity_free_magnitude(double g, bool crash)
    {
        var detector = new CrashDetector();
        detector.OnSpeed(0, 12);
        detector.OnLinearAcceleration(1, 0, 0, g * G);
        detector.OnSpeed(2, 0.2);
        Assert.Equal(crash, detector.OnSpeed(3, 0.1));
    }

    [Fact]
    public void SOS_022_frontal_impact_on_the_X_axis_with_the_phone_upright_triggers()
    {
        var detection = Assert.Single(SosTraces.Replay("sos022_xaxis_upright_mount.txt"));
        Assert.Equal(EmergencyAlert.Crash, detection.TriggerType);
    }

    [Fact]
    public void SOS_022_spike_uses_the_magnitude_of_all_three_axes()
    {
        // 2.4 G on each axis: no single axis is above 4 G, the magnitude (4.16 G) is.
        var detector = new CrashDetector();
        detector.OnSpeed(0, 12);
        detector.OnLinearAcceleration(1, 2.4 * G, 2.4 * G, 2.4 * G);
        detector.OnSpeed(2, 0.1);
        Assert.True(detector.OnSpeed(3, 0.0));
    }

    [Fact]
    public void SOS_023_stop_after_the_10_second_window_is_not_a_crash()
    {
        Assert.Empty(SosTraces.Replay("sos023_stop_after_12s.txt"));
    }

    [Fact]
    public void SOS_018_one_stopped_fix_is_not_enough_and_a_moving_fix_resets_the_count()
    {
        var detector = new CrashDetector();
        detector.OnSpeed(0, 12);
        detector.OnLinearAcceleration(1, 0, 0, 5 * G);
        Assert.False(detector.OnSpeed(2, 0.1));
        Assert.False(detector.OnSpeed(3, 3.0));  // rolled on: count starts again
        Assert.False(detector.OnSpeed(4, 0.1));
        Assert.True(detector.OnSpeed(5, 0.1));
    }

    [Fact]
    public void SOS_018_fixes_without_speed_are_ignored()
    {
        var detector = new CrashDetector();
        detector.OnSpeed(0, 12);
        detector.OnLinearAcceleration(1, 0, 0, 5 * G);
        Assert.False(detector.OnSpeed(2, null));
        Assert.False(detector.OnSpeed(3, null));
        Assert.False(detector.OnSpeed(4, 0.1));
        Assert.True(detector.OnSpeed(5, 0.1));
    }

    // ---- Shared pipeline ----------------------------------------------------

    [Fact]
    public void Gravity_filter_reads_a_still_phone_as_no_movement()
    {
        var filter = new GravityFilter();
        (double x, double y, double z) = (1, 1, 1);
        for (int i = 0; i < 50; i++) (x, y, z) = filter.Linear(0, 0, (float)G);
        Assert.Equal(0, Math.Sqrt(x * x + y * y + z * z), 3);
    }

    [Fact]
    public void Sensor_trace_parses_accel_and_gps_lines_including_fixes_without_speed()
    {
        SensorTrace trace = SensorTrace.Parse("# c\na,0.000,0,0,9.81\ng,0.500,\ng,1.500,3.25\n");
        Assert.Equal(3, trace.Samples.Count);
        Assert.Null(trace.Samples[1].Speed);
        Assert.Equal(3.25, trace.Samples[2].Speed);
        Assert.Throws<FormatException>(() => SensorTrace.Parse("x,1,2"));
    }
}
