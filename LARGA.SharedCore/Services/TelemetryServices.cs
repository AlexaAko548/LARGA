using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices.Sensors;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;

namespace LARGA.SharedCore.Services;

/// <summary>
/// LAR-77: rebuild of the old (dead, never-wired, wrong-collection-name) TelemetryServices.cs.
/// This is the driver app's background GPS writer - it has no manager-facing reads, those
/// already exist in FleetMapViewModel / FleetReportingService / FuelVerificationService, all
/// of which read "gps_telemetry" expecting {shiftId, latitude, longitude, speed, timestamp}.
///
/// Lifecycle is the "Contextual Auto-Cutoff Protocol" from the ticket: Start(shiftId) is
/// called right after ClockInAsync succeeds, Stop() right after ClockOutAsync succeeds
/// (PreShiftStep2ViewModel / ClockInPendingViewModel / EndShiftStep2ViewModel). The driver
/// dashboard also re-starts it for a shift that's still open after the app was restarted, and
/// stops it when the shift has ended elsewhere or the driver logs out. Registered as a singleton so the poll
/// loop survives navigating away from the Active Shift page - it only stops when the shift
/// itself ends, not when the page closes.
///
/// Scope note: this polls on an app-level timer while the process is alive (foreground or
/// backgrounded-but-not-killed by the OS) - it is NOT a true Android foreground service with
/// a persistent notification, so the OS can still suspend it if the app is swiped away or
/// killed outright. True always-on background tracking is a much larger platform-specific
/// task; flagged as a follow-up rather than silently claimed here.
/// </summary>
public interface IGpsTelemetryService
{
    void Start(string shiftId);
    void Stop();
}

public class GpsTelemetryService : IGpsTelemetryService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LocationTimeout = TimeSpan.FromSeconds(15);

    private CancellationTokenSource? _cts;
    private string? _runningShiftId;

    public void Start(string shiftId)
    {
        // Already tracking this shift (e.g. the dashboard re-syncing an open shift): keep the
        // running loop instead of restarting it and writing an extra point.
        if (_cts is not null && _runningShiftId == shiftId)
        {
            return;
        }

        // Guard against a stray double clock-in leaking a second, undisposed loop.
        Stop();

        if (string.IsNullOrWhiteSpace(shiftId))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        _runningShiftId = shiftId;
        _ = RunAsync(shiftId, cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _runningShiftId = null;
    }

    private static async Task RunAsync(string shiftId, CancellationToken token)
    {
        // First point lands immediately so the Fleet Map gets a pin as soon as the shift
        // starts, instead of waiting a full PollInterval for the initial fix.
        await CaptureAndWriteAsync(shiftId, token);

        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(token))
            {
                await CaptureAndWriteAsync(shiftId, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: Stop() was called from ClockOutAsync's success path.
        }
    }

    private static async Task CaptureAndWriteAsync(string shiftId, CancellationToken token)
    {
        try
        {
            PermissionStatus status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            }
            if (status != PermissionStatus.Granted)
            {
                return;
            }

            // Real device hardware only - never a mocked/simulated fallback per the ticket.
            // GetLastKnownLocationAsync below is still a real, previously-captured GPS fix,
            // not synthetic data; it's a fallback for a slow/denied fresh read only.
            Location? location = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.Medium, LocationTimeout), token);
            location ??= await Geolocation.Default.GetLastKnownLocationAsync();

            if (location == null)
            {
                return;
            }

            // firestore.rules only accept a point from the driver who owns the shift.
            string? driverId = CrossFirebaseAuth.Current.CurrentUser?.Uid;
            if (string.IsNullOrEmpty(driverId))
            {
                return;
            }

            var point = new GpsTelemetryProxy
            {
                ShiftId = shiftId,
                DriverId = driverId,
                Latitude = location.Latitude,
                Longitude = location.Longitude,
                Speed = (int)Math.Round((location.Speed ?? 0) * 3.6), // m/s -> km/h
                Timestamp = DateTime.UtcNow,
            };

            await CrossFirebaseFirestore.Current
                .GetCollection("gps_telemetry")
                .AddDocumentAsync(point);
        }
        catch (OperationCanceledException)
        {
            throw; // let RunAsync's loop see cancellation and exit cleanly
        }
        catch (Exception ex)
        {
            // One bad reading or a transient Firestore hiccup shouldn't end telemetry for
            // the rest of the shift - log and let the next tick try again.
            System.Diagnostics.Debug.WriteLine($"GPS telemetry tick failed: {ex.Message}");
        }
    }

    private class GpsTelemetryProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")]
        public string ShiftId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("driverId")]
        public string DriverId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("latitude")]
        public double Latitude { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("longitude")]
        public double Longitude { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("speed")]
        public int Speed { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("timestamp")]
        public DateTime Timestamp { get; set; }
    }
}
