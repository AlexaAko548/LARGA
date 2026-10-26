using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Emergency;

namespace LARGA.Tests.Sos;

/// <summary>
/// M7 SOS Emergency - countdown gate, auto-answer of the manager's callback (window, caller check,
/// audio save/restore), SOS location fallback, caller validation and the push repeat filter.
/// </summary>
public class SosProtocolTests
{
    private const string Manager = "+639171234567";
    private static readonly DateTime T0 = new(2026, 10, 24, 2, 0, 0, DateTimeKind.Utc);

    private static (EmergencyCallAutoAnswer State, FakeCallAudio Audio) Armed(EmergencyCallMode mode)
    {
        var audio = new FakeCallAudio();
        var state = new EmergencyCallAutoAnswer(audio);
        state.Arm(mode, new[] { Manager }, T0);
        return (state, audio);
    }

    /// <summary>The broadcasts of an answered call: ringing (twice, once without the number), off-hook.</summary>
    private static void ManagerCallConnects(EmergencyCallAutoAnswer state, string number, DateTime at)
    {
        Assert.Equal(RingingDecision.Ignore, state.OnRinging(null, at));
        Assert.Equal(RingingDecision.Answer, state.OnRinging(number, at));
        state.BeginAnswer();
        Assert.Equal(RingingDecision.Ignore, state.OnRinging(number, at)); // duplicate broadcast while answering
        Assert.True(state.OnOffhook());
        state.ApplyCallAudio();
    }

    // ---- Countdown gate -----------------------------------------------------

    [Fact]
    public void SOS_009_cancel_within_5_seconds_means_no_alert()
    {
        var gate = new EmergencyCountdownGate();
        Assert.True(gate.TryBegin(EmergencyAlert.Hostile, out CancellationToken token));

        Assert.True(gate.Cancel());

        Assert.True(token.IsCancellationRequested);
        Assert.False(gate.TryComplete()); // the timer firing afterwards sends nothing
        Assert.Null(gate.Active);
    }

    [Fact]
    public void SOS_009_cancel_after_the_countdown_ran_out_does_nothing()
    {
        var gate = new EmergencyCountdownGate();
        gate.TryBegin(EmergencyAlert.Hostile, out _);
        Assert.True(gate.TryComplete());
        Assert.False(gate.Cancel());
    }

    [Fact]
    public void SOS_026_second_detection_during_a_countdown_is_ignored()
    {
        var gate = new EmergencyCountdownGate();
        Assert.True(gate.TryBegin(EmergencyAlert.Hostile, out _));

        Assert.False(gate.TryBegin(EmergencyAlert.Crash, out _));
        Assert.False(gate.TryBegin(EmergencyAlert.Hostile, out _));
        Assert.Equal(EmergencyAlert.Hostile, gate.Active);
    }

    [Fact]
    public void SOS_026_second_detection_while_the_first_alert_is_being_written_is_ignored()
    {
        var gate = new EmergencyCountdownGate();
        gate.TryBegin(EmergencyAlert.Hostile, out _);
        Assert.True(gate.TryComplete());          // countdown ran out - the alert is being written

        Assert.True(gate.Sending);
        Assert.False(gate.TryBegin(EmergencyAlert.Crash, out _));
        Assert.False(gate.Cancel());              // too late to cancel
        Assert.Equal(EmergencyAlert.Hostile, gate.Active);

        gate.Finish();                            // written: a crash now gets its own countdown (SOS-025)
        Assert.True(gate.TryBegin(EmergencyAlert.Crash, out _));
    }

    [Fact]
    public void SOS_026_cancel_and_expiry_racing_only_one_wins()
    {
        for (int run = 0; run < 200; run++)
        {
            var gate = new EmergencyCountdownGate();
            gate.TryBegin(EmergencyAlert.Crash, out _);
            int winners = 0;
            Parallel.Invoke(
                () => { if (gate.Cancel()) Interlocked.Increment(ref winners); },
                () => { if (gate.TryComplete()) Interlocked.Increment(ref winners); });
            Assert.Equal(1, winners);
        }
    }

    // ---- Auto-answer --------------------------------------------------------

    [Fact]
    public void SOS_013_manager_callback_is_answered_muted_and_audio_restored_after()
    {
        var (state, audio) = Armed(EmergencyCallMode.Stealth);
        var before = (Ring: 5, Voice: 3, Speaker: false, Muted: false);

        Assert.Equal(0, audio.Volume[CallAudioStream.Ring]); // ringer at minimum before the call

        ManagerCallConnects(state, "0917 123 4567", T0.AddMinutes(10)); // typed differently, same number

        Assert.False(audio.SpeakerphoneOn);                         // earpiece
        Assert.Equal(1, audio.Volume[CallAudioStream.VoiceCall]);   // stream minimum
        Assert.True(audio.VoiceMuted);

        Assert.True(state.OnIdle());                                // manager hangs up
        Assert.Equal(before, audio.Snapshot());
        Assert.Null(state.Mode);                                    // one call per emergency
    }

    [Fact]
    public void SOS_013_manager_hanging_up_before_the_answer_still_gets_the_next_call_answered()
    {
        var (state, audio) = Armed(EmergencyCallMode.Stealth);
        Assert.Equal(RingingDecision.Answer, state.OnRinging(Manager, T0.AddMinutes(1)));
        state.BeginAnswer();
        Assert.False(state.OnIdle());             // rang off before it was picked up

        // Before the fix "answering" stayed set: this call was ignored, and the window never expired.
        Assert.Equal(RingingDecision.Answer, state.OnRinging(Manager, T0.AddMinutes(2)));
        state.AnswerFailed();
        Assert.True(state.DisarmIfExpired(T0.AddMinutes(31)));
        Assert.Equal((5, 3, false, false), audio.Snapshot());
    }

    [Fact]
    public void SOS_014_call_from_another_number_rings_normally_and_is_not_answered()
    {
        var (state, audio) = Armed(EmergencyCallMode.Stealth);

        Assert.Equal(RingingDecision.NotManager, state.OnRinging("09998887777", T0.AddMinutes(1)));
        Assert.False(state.OnOffhook()); // the driver picking it up themself isn't the handled call
        Assert.False(state.OnIdle());
        Assert.Equal(EmergencyCallMode.Stealth, state.Mode); // still waiting for the manager
    }

    [Fact]
    public void SOS_015_manager_calling_31_minutes_later_is_not_answered_and_audio_is_restored()
    {
        var (state, audio) = Armed(EmergencyCallMode.Stealth);

        Assert.Equal(RingingDecision.Expired, state.OnRinging(Manager, T0.AddMinutes(31)));

        Assert.Null(state.Mode);
        Assert.Equal((5, 3, false, false), audio.Snapshot());
        Assert.Equal(RingingDecision.Ignore, state.OnRinging(Manager, T0.AddMinutes(31)));
    }

    [Fact]
    public void SOS_015_window_timer_disarms_after_30_minutes()
    {
        var (state, audio) = Armed(EmergencyCallMode.Stealth);
        Assert.False(state.DisarmIfExpired(T0.AddMinutes(29)));
        Assert.True(state.DisarmIfExpired(T0.AddMinutes(30).AddSeconds(1)));
        Assert.Equal((5, 3, false, false), audio.Snapshot());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void SOS_016_missing_phone_permissions_block_arming(bool phoneState, bool callLog)
    {
        Assert.NotNull(EmergencyCallAutoAnswer.ArmBlockedReason(phoneState, callLog, true, null));
        Assert.Null(EmergencyCallAutoAnswer.ArmBlockedReason(true, true, true, null));
    }

    [Fact]
    public void SOS_016_without_answer_phone_calls_it_is_not_armed_so_the_ringer_is_left_alone()
    {
        // Arming would only have silenced the ringer for 30 minutes - the call can't be picked up.
        Assert.Contains("ANSWER_PHONE_CALLS", EmergencyCallAutoAnswer.ArmBlockedReason(true, true, false, null));
    }

    [Fact]
    public void SOS_017_no_manager_numbers_blocks_arming()
    {
        Assert.Contains("managerPhoneNumbers", EmergencyCallAutoAnswer.ArmBlockedReason(true, true, true, 0));
        Assert.Null(EmergencyCallAutoAnswer.ArmBlockedReason(true, true, true, 1));
    }

    [Fact]
    public void SOS_024_crash_callback_is_answered_on_speaker_at_max_volume()
    {
        var (state, audio) = Armed(EmergencyCallMode.Loud);
        Assert.Equal(5, audio.Volume[CallAudioStream.Ring]); // Loud leaves the ringer alone

        ManagerCallConnects(state, Manager, T0.AddMinutes(2));

        Assert.True(audio.SpeakerphoneOn);
        Assert.Equal(5, audio.Volume[CallAudioStream.VoiceCall]);
        Assert.False(audio.VoiceMuted);

        state.OnIdle();
        Assert.Equal((5, 3, false, false), audio.Snapshot());
    }

    [Fact]
    public void SOS_025_crash_during_an_armed_hostile_window_switches_to_loud_and_restores_the_original_audio()
    {
        var (state, audio) = Armed(EmergencyCallMode.Stealth);

        state.Arm(EmergencyCallMode.Loud, new[] { Manager }, T0.AddMinutes(5));
        Assert.Equal(EmergencyCallMode.Loud, state.Mode);
        Assert.Equal(5, audio.Volume[CallAudioStream.Ring]); // the crash callback rings out again

        ManagerCallConnects(state, Manager, T0.AddMinutes(6));
        Assert.True(audio.SpeakerphoneOn);
        Assert.Equal(5, audio.Volume[CallAudioStream.VoiceCall]);

        state.OnIdle();
        Assert.Equal((5, 3, false, false), audio.Snapshot()); // the driver's settings, not the stealth ones
    }

    [Fact]
    public void SOS_027_arming_after_the_monitor_stopped_is_refused()
    {
        var (state, audio) = Armed(EmergencyCallMode.Stealth);
        Assert.True(state.ShutDown());            // clock-out while armed: audio back to normal
        Assert.Equal((5, 3, false, false), audio.Snapshot());

        // An alert that was still being written at clock-out tries to arm afterwards.
        Assert.False(state.Arm(EmergencyCallMode.Stealth, new[] { Manager }, T0.AddMinutes(1)));
        Assert.Null(state.Mode);
        Assert.Equal(5, audio.Volume[CallAudioStream.Ring]);
        Assert.Equal(RingingDecision.Ignore, state.OnRinging(Manager, T0.AddMinutes(2)));
    }

    [Fact]
    public void SOS_013_phone_numbers_are_masked_in_the_log()
    {
        Assert.Equal("*********4567", EmergencyCallAutoAnswer.Mask("+639171234567"));
        Assert.Equal("****", EmergencyCallAutoAnswer.Mask("123"));
    }

    // ---- Location (SOS-003) -------------------------------------------------

    [Fact]
    public void SOS_003_live_fix_is_used_when_there_is_one()
    {
        var p = SosLocation.Resolve((10.3, 123.9), (10.0, 123.0));
        Assert.Equal((10.3, 123.9, SosLocation.SourceLive), (p.Latitude, p.Longitude, p.Source));
    }

    [Fact]
    public void SOS_003_location_denied_falls_back_to_the_last_shift_gps_point()
    {
        var p = SosLocation.Resolve(null, (10.0, 123.0));
        Assert.Equal((10.0, 123.0, SosLocation.SourceLastShiftFix), (p.Latitude, p.Longitude, p.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SOS_003_no_fix_at_all_sends_0_0_with_source_none(bool lastFixIsZero)
    {
        var p = SosLocation.Resolve(null, lastFixIsZero ? (0, 0) : null);
        Assert.Equal((0.0, 0.0, SosLocation.SourceNone), (p.Latitude, p.Longitude, p.Source));
        Assert.False(SosLocation.HasLocation(p.Latitude, p.Longitude)); // "Location unavailable" on dispatch
    }

    // ---- Caller validation (SOS-006) ----------------------------------------

    private static ShiftLog ActiveShift(string driverId = "drvA") =>
        new() { ShiftId = "shiftA", DriverId = driverId, TaxiId = "TX-01", Status = "Active" };

    private static UserProfile DriverA() => new() { UserId = "drvA", PhoneNumber = "09170000001" };

    private static EmergencyAlert AlertFromA(string? callerPhone = "+639170000001", string? taxiId = "TX-01") =>
        new() { ShiftId = "shiftA", DriverId = "drvA", CallerPhone = callerPhone, TaxiId = taxiId };

    [Fact]
    public void SOS_006_assigned_driver_from_their_registered_number_is_verified()
    {
        var r = SosCallerValidator.Validate(AlertFromA(), ActiveShift(), DriverA());
        Assert.Equal(SosCallerCheck.Verified, r.Status);
    }

    [Fact]
    public void SOS_006_older_build_without_phone_is_unverified_and_still_pushed()
    {
        var r = SosCallerValidator.Validate(AlertFromA(callerPhone: null, taxiId: null), ActiveShift(), DriverA());
        Assert.Equal(SosCallerCheck.Unverified, r.Status);
        Assert.False(r.Rejected);
    }

    [Fact]
    public void SOS_006_alert_for_another_drivers_shift_is_rejected()
    {
        var r = SosCallerValidator.Validate(AlertFromA(), ActiveShift(driverId: "drvB"), DriverA());
        Assert.Equal(SosCallerCheck.Rejected, r.Status);
    }

    [Fact]
    public void SOS_006_registered_number_mismatch_is_rejected()
    {
        var r = SosCallerValidator.Validate(AlertFromA(callerPhone: "09179999999"), ActiveShift(), DriverA());
        Assert.Equal(SosCallerCheck.Rejected, r.Status);
    }

    [Theory]
    [InlineData("shift ended")]
    [InlineData("no profile")]
    [InlineData("unit mismatch")]
    [InlineData("sim mismatch")]
    [InlineData("no shift")]
    public void SOS_006_soft_mismatches_are_unverified_not_rejected(string variant)
    {
        EmergencyAlert alert = AlertFromA();
        ShiftLog? shift = ActiveShift();
        UserProfile? driver = DriverA();
        switch (variant)
        {
            case "shift ended": shift.Status = "Completed"; shift.ShiftEnd = T0; break;
            case "no profile": driver = null; break;
            case "unit mismatch": alert.TaxiId = "TX-09"; break;
            case "sim mismatch": alert.DevicePhone = "09175555555"; break;
            case "no shift": shift = null; break;
        }

        Assert.Equal(SosCallerCheck.Unverified, SosCallerValidator.Validate(alert, shift, driver).Status);
    }

    // ---- Resolve (SOS-007) ---------------------------------------------------

    [Theory]
    [InlineData("Manager", true)]
    [InlineData(" manager ", true)]
    [InlineData("Assistant Manager", false)]
    [InlineData("Driver", false)]
    [InlineData(null, false)]
    public void SOS_007_only_a_manager_can_resolve(string? role, bool allowed)
    {
        Assert.Equal(allowed, SosResolvePolicy.CanResolve(role));
    }

    // ---- Push age (SOS-004) -------------------------------------------------

    [Fact]
    public void SOS_004_sos_queued_offline_for_over_30_minutes_is_still_pushed_on_reconnect()
    {
        DateTime now = T0;
        // Held 45 minutes ago with no signal, reached the server just now.
        Assert.False(SosPushPolicy.IsTooOldToPush(serverCreatedAtUtc: now.AddSeconds(-2), phoneTimestampUtc: now.AddMinutes(-45), now));
    }

    [Fact]
    public void SOS_004_phone_clock_running_behind_does_not_block_the_push()
    {
        Assert.False(SosPushPolicy.IsTooOldToPush(T0.AddSeconds(-1), T0.AddHours(-2), T0));
    }

    [Fact]
    public void SOS_004_alerts_already_old_on_the_server_when_first_seen_are_not_pushed()
    {
        Assert.True(SosPushPolicy.IsTooOldToPush(T0.AddMinutes(-31), T0.AddMinutes(-31), T0));
        Assert.True(SosPushPolicy.IsTooOldToPush(null, T0.AddMinutes(-31), T0)); // no create time: phone time
    }

    // ---- Push repeat filter (SOS-005) ---------------------------------------

    [Fact]
    public void SOS_005_repeat_sos_from_the_same_driver_within_a_minute_is_not_pushed_again()
    {
        var filter = new SosRepeatPushFilter();
        filter.Remember("drvA", EmergencyTypes.Severity(EmergencyAlert.Standard), T0);

        Assert.True(filter.IsRepeat("drvA", EmergencyTypes.Severity(EmergencyAlert.Standard), T0.AddSeconds(10)));
        Assert.False(filter.IsRepeat("drvA", EmergencyTypes.Severity(EmergencyAlert.Crash), T0.AddSeconds(10))); // more severe
        Assert.False(filter.IsRepeat("drvB", EmergencyTypes.Severity(EmergencyAlert.Standard), T0.AddSeconds(10)));
        Assert.False(filter.IsRepeat("drvA", EmergencyTypes.Severity(EmergencyAlert.Standard), T0.AddSeconds(61)));
    }
}
