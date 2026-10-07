using System;
using System.Collections.Generic;
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
///
/// Storage: there's no deletion code here on purpose. gps_telemetry has a Firestore TTL
/// policy (Cloud Console > Firestore > Time-to-live, not Firebase Console - it's a separate
/// product UI over the same project) on the "timestamp" field, offset 7 days - Firestore
/// deletes documents past that age in the background at no extra read/write cost. No feature
/// reads telemetry older than its own shift (FleetReportingService wants only the latest
/// point, FuelVerificationService only queries within [shiftStart, now]), so a week is just a
/// buffer for manual review after a shift ends, not something any code depends on.
/// </summary>
public interface IGpsTelemetryService
{
    void Start(string shiftId);
    void Stop();

    /// <summary>
    /// GPS distance covered so far in the running shift, in km (0 when no shift is tracked).
    /// Summed from the same 30s fixes written to gps_telemetry, so it reads a little under the
    /// odometer on winding routes - it's a live indicator, endMileage stays the billing figure.
    /// </summary>
    double CurrentDistanceKm { get; }

    /// <summary>Raised (off the UI thread) with the new total in km after a fix adds distance.</summary>
    event EventHandler<double>? DistanceChanged;

    /// <summary>Whether the last point written showed the unit moving - false before the first
    /// point. The Fleet Map shows a unit Idle by this same reading (speed 0), so the driver's
    /// own screen can say so too.</summary>
    bool IsMoving { get; }

    /// <summary>Raised (on a background thread) when IsMoving changes.</summary>
    event EventHandler? MovementChanged;
}

public class GpsTelemetryService : IGpsTelemetryService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LocationTimeout = TimeSpan.FromSeconds(15);

    // Distance filters: a fix worse than MaxAccuracyMeters is still written for the Fleet Map
    // but not trusted for distance, and hops under MinSegmentMeters are treated as GPS jitter
    // (a parked taxi otherwise "drives" a few hundred meters an hour).
    private const double MaxAccuracyMeters = 50;
    private const double MinSegmentMeters = 20;

    // A fix older than this is the phone repeating a cached position (nothing new came in) -
    // its speed and heading are from when it was taken, not now.
    private static readonly TimeSpan StaleFixAge = TimeSpan.FromSeconds(45);

    // Less than this between two fixes ~30s apart counts as stopped: it's within GPS drift
    // for a parked car, and anything slower than ~3 km/h isn't worth animating on the map.
    private const double MinMovementMeters = 25;

    private CancellationTokenSource? _cts;
    private string? _runningShiftId;
    // Per-run state, swapped on Start/Stop, so a cancelled loop's in-flight tick can't add
    // distance into the next shift's total.
    private DistanceTracker? _tracker;

    public double CurrentDistanceKm => (_tracker?.Meters ?? 0) / 1000.0;

    public event EventHandler<double>? DistanceChanged;

    private bool _isMoving;

    public bool IsMoving => _isMoving;

    public event EventHandler? MovementChanged;

    private void SetMoving(bool moving)
    {
        if (_isMoving == moving) return;
        _isMoving = moving;
        MovementChanged?.Invoke(this, EventArgs.Empty);
    }

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
        var tracker = new DistanceTracker();
        _cts = cts;
        _runningShiftId = shiftId;
        _tracker = tracker;
        _ = RunAsync(shiftId, tracker, cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _runningShiftId = null;
        _tracker = null;
        SetMoving(false);
    }

    private async Task RunAsync(string shiftId, DistanceTracker tracker, CancellationToken token)
    {
        // Resume the total already saved on the shift doc, so restarting the app mid-shift
        // (the dashboard calls Start again) doesn't reset the driver's distance to 0.
        await SeedDistanceAsync(shiftId, tracker);

        try
        {
            // First point lands immediately so the Fleet Map gets a pin as soon as the shift
            // starts, instead of waiting a full PollInterval for the initial fix.
            ReportMoving(await CaptureAndWriteAsync(shiftId, tracker, token), tracker);

            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(token))
            {
                ReportMoving(await CaptureAndWriteAsync(shiftId, tracker, token), tracker);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: Stop() was called from ClockOutAsync's success path.
        }
    }

    private void ReportMoving(bool? moving, DistanceTracker tracker)
    {
        if (moving is bool value && ReferenceEquals(tracker, _tracker))
        {
            SetMoving(value);
        }
    }

    private async Task SeedDistanceAsync(string shiftId, DistanceTracker tracker)
    {
        try
        {
            var snapshot = await CrossFirebaseFirestore.Current
                .GetCollection("shifts")
                .GetDocument(shiftId)
                .GetDocumentSnapshotAsync<ShiftDistanceProxy>();

            double savedKm = snapshot?.Data?.DistanceKm ?? 0;
            if (savedKm > 0 && tracker.Meters == 0)
            {
                tracker.Meters = savedKm * 1000;
                if (ReferenceEquals(tracker, _tracker))
                {
                    DistanceChanged?.Invoke(this, savedKm);
                }
            }
        }
        catch (Exception ex)
        {
            // Not fatal: the shift just counts from 0 again.
            System.Diagnostics.Debug.WriteLine($"GPS distance seed failed: {ex.Message}");
        }
    }

    /// <summary>Writes one point and returns whether it showed the unit moving - or null when
    /// nothing was written.</summary>
    private async Task<bool?> CaptureAndWriteAsync(string shiftId, DistanceTracker tracker, CancellationToken token)
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
                return null;
            }

            // Real device hardware only - never a mocked/simulated fallback per the ticket.
            // GetLastKnownLocationAsync below is still a real, previously-captured GPS fix,
            // not synthetic data; it's a fallback for a slow/denied fresh read only.
            Location? location = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.Medium, LocationTimeout), token);
            location ??= await Geolocation.Default.GetLastKnownLocationAsync();

            if (location == null)
            {
                return null;
            }

            // firestore.rules only accept a point from the driver who owns the shift.
            string? driverId = CrossFirebaseAuth.Current.CurrentUser?.Uid;
            if (string.IsNullOrEmpty(driverId))
            {
                return null;
            }

            Location? previous = tracker.LastWrittenFix;
            bool moving = HasMovedSince(location, previous, DateTimeOffset.UtcNow);
            int speedKmh = moving ? (int)Math.Round(SpeedMetersPerSecond(location, previous) * 3.6) : 0; // m/s -> km/h
            // -1 = "no heading": many fixes (Medium accuracy on Android especially) come back
            // without a course, and 0 would read as due north - the Fleet Map would then
            // dead-reckon the pin north regardless of where the taxi is actually heading.
            // Stored as a sentinel rather than null since the map's Firestore proxy can't
            // deserialize nullable values reliably on Android.
            // A stopped unit has no heading either, so the map doesn't project it anywhere.
            double headingDegrees = speedKmh > 0 && location.Course is double course && course >= 0 && !double.IsNaN(course)
                ? course
                : -1;
            var now = DateTime.UtcNow;

            var point = new GpsTelemetryProxy
            {
                ShiftId = shiftId,
                DriverId = driverId,
                Latitude = location.Latitude,
                Longitude = location.Longitude,
                Speed = speedKmh,
                Timestamp = now,
            };

            await CrossFirebaseFirestore.Current
                .GetCollection("gps_telemetry")
                .AddDocumentAsync(point);

            // Denormalized onto the shift doc itself (separate from the gps_telemetry trail
            // above, which stays append-only for history/distance calculations) so the Live
            // Fleet map can hold a single live listener on "shifts" instead of a separate
            // listener per active shift, or an unbounded listener over all of gps_telemetry.
            // Heading lets the map dead-reckon the pin between writes instead of it sitting
            // frozen for the full 30s gap.
            bool distanceChanged = tracker.Add(location);

            // distanceKm rides along on the same update - the live total for the Active Shift
            // screen, and what SeedDistanceAsync resumes from after an app restart.
            await CrossFirebaseFirestore.Current
                .GetCollection("shifts")
                .GetDocument(shiftId)
                .UpdateDataAsync(new Dictionary<object, object>
                {
                    { "currentLatitude", location.Latitude },
                    { "currentLongitude", location.Longitude },
                    { "currentSpeed", speedKmh },
                    { "currentHeading", headingDegrees },
                    { "currentPositionUpdatedAt", now },
                    { "distanceKm", Math.Round(tracker.Meters / 1000.0, 2) },
                });

            if (distanceChanged && ReferenceEquals(tracker, _tracker))
            {
                DistanceChanged?.Invoke(this, tracker.Meters / 1000.0);
            }

            tracker.LastWrittenFix = location;
            return speedKmh > 0;
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
            return null;
        }
    }

    /// <summary>
    /// Whether the unit is really moving. The phone's reported speed alone isn't enough: when no
    /// new fix comes in (the car stopped indoors, or a route simulation was paused), Android keeps
    /// handing back the last fix - speed and heading included - so a parked unit would keep
    /// reporting, say, 40 km/h and the Fleet Map would keep animating it forward and back.
    /// </summary>
    private static bool HasMovedSince(Location location, Location? previous, DateTimeOffset nowUtc)
    {
        if (nowUtc - location.Timestamp > StaleFixAge)
        {
            return false; // a cached fix, not a current one
        }

        if (previous is null)
        {
            return (location.Speed ?? 0) > 0; // first point of the shift: all we have is the phone's word
        }

        if (location.Timestamp <= previous.Timestamp)
        {
            return false; // the same fix again
        }

        return MetersBetween(previous, location) >= MinMovementMeters;
    }

    /// <summary>The phone's reported speed, or - when it reports none - the distance covered
    /// since the previous fix over the time between them.</summary>
    private static double SpeedMetersPerSecond(Location location, Location? previous)
    {
        if (location.Speed is double reported && reported > 0 && !double.IsNaN(reported))
        {
            return reported;
        }

        if (previous is null)
        {
            return 0;
        }

        double seconds = (location.Timestamp - previous.Timestamp).TotalSeconds;
        return seconds > 0 ? MetersBetween(previous, location) / seconds : 0;
    }

    private static double MetersBetween(Location a, Location b) =>
        Location.CalculateDistance(a, b, DistanceUnits.Kilometers) * 1000;


    /// <summary>
    /// Running GPS distance for one shift: sums haversine hops between accepted fixes.
    /// Only touched from that shift's RunAsync loop, which awaits each tick in turn.
    /// </summary>
    private sealed class DistanceTracker
    {
        private Location? _lastFix;

        public double Meters { get; set; }

        /// <summary>The fix behind the last point written - what the next tick compares
        /// against to tell whether the unit actually moved.</summary>
        public Location? LastWrittenFix { get; set; }

        /// <returns>true when this fix added distance.</returns>
        public bool Add(Location fix)
        {
            if (fix.Accuracy is double accuracy && accuracy > MaxAccuracyMeters)
            {
                return false;
            }

            // GetLastKnownLocationAsync can hand back the same fix twice.
            if (_lastFix != null && _lastFix.Timestamp == fix.Timestamp)
            {
                return false;
            }

            if (_lastFix == null)
            {
                _lastFix = fix;
                return false;
            }

            double hop = ShiftRules.DistanceMeters(
                _lastFix.Latitude, _lastFix.Longitude, fix.Latitude, fix.Longitude);
            if (hop < MinSegmentMeters)
            {
                // Keep the old anchor so slow creeping still adds up once it passes 20 m.
                return false;
            }

            Meters += hop;
            _lastFix = fix;
            return true;
        }
    }

    private class ShiftDistanceProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("distanceKm")]
        public double DistanceKm { get; set; }
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
