using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Storage;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using LARGA.SharedCore;
using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.Services;

/// <summary>
/// The one place the driver app writes to emergency_alerts - the manual SOS button and the
/// LAR-86/87 automated Hostile/Crash protocols both go through here, so every alert carries the
/// same payload (driver, taxi unit, timestamp, GPS) plus its triggerType.
/// </summary>
public interface IEmergencyAlertService
{
    /// <summary>
    /// Writes an alert for the driver's active shift. <paramref name="triggerType"/> is one of
    /// EmergencyAlert.Standard / Hostile / Crash. When no coordinates are passed the device's
    /// location is looked up; failing that, the shift's last GPS point is used; failing that, the
    /// alert still goes out without a location (0/0, locationSource "none") - an SOS is never
    /// dropped for lack of GPS. Returns the new document's ID, or null only when there's no
    /// active shift. <paramref name="tryLiveFix"/> false skips the live lookup (the caller
    /// already tried one).
    /// </summary>
    Task<string?> SendAlertAsync(string triggerType, double? latitude = null, double? longitude = null, bool tryLiveFix = true);

    /// <summary>
    /// Looks up the driver's name and today's unit for this shift ahead of time (Active Shift
    /// screen), so an SOS doesn't wait on those reads - with weak signal they're the slow part.
    /// Never throws.
    /// </summary>
    Task PrepareForShiftAsync(string shiftId);

    /// <summary>Where the last alert's coordinates came from: "live", "lastShiftFix" or "none".</summary>
    string LastLocationSource { get; }

    /// <summary>True when the last alert couldn't be confirmed by the server in time (no signal).
    /// It's saved on the phone and goes out by itself once the phone reconnects.</summary>
    bool LastSendQueued { get; }

    /// <summary>
    /// system_configs/global.managerPhoneNumbers, normalized to +639XXXXXXXXX. Drivers can't
    /// read managers' users documents (firestore.rules), so this list is kept there instead.
    /// </summary>
    Task<IReadOnlyList<string>> GetManagerPhoneNumbersAsync();
}

/// <summary>
/// LAR-86/87 automated emergency detection (Hostile shake / Crash) and manager call handling.
/// Runs for exactly as long as a shift is open - started and stopped next to
/// IGpsTelemetryService.Start/Stop.
/// </summary>
public interface IEmergencyMonitor
{
    /// <summary>Starts monitoring for this shift; a no-op if it's already running for it.</summary>
    Task StartAsync(string shiftId);

    void Stop();
}

/// <summary>
/// Driver-facing feedback for SOS presses and alerts: vibration, and the crash alarm sound.
/// Hostile alerts stay silent on purpose, so only vibration is used for them.
/// </summary>
public interface IEmergencyFeedback
{
    /// <summary>Short pulse once a manual SOS press completes, so the driver knows to let go.</summary>
    void ButtonHeld();

    /// <summary>Heads-up that an SOS was written, whatever started it. Crash also sounds the alarm.</summary>
    void AlertSent(string triggerType);
}

/// <summary>
/// Automated emergency detection needs Android's sensor, foreground-service and telephony APIs;
/// on other platforms the driver still has the manual SOS button.
/// </summary>
public class NoOpEmergencyMonitor : IEmergencyMonitor
{
    public Task StartAsync(string shiftId) => Task.CompletedTask;

    public void Stop()
    {
    }
}

public class NoOpEmergencyFeedback : IEmergencyFeedback
{
    public void ButtonHeld()
    {
    }

    public void AlertSent(string triggerType)
    {
    }
}

public class EmergencyAlertService : IEmergencyAlertService
{
    // emergency_alerts.locationSource values.
    public const string LocationSourceLive = "live";
    public const string LocationSourceLastShiftFix = "lastShiftFix";
    public const string LocationSourceNone = "none";

    public string LastLocationSource { get; private set; } = LocationSourceLive;

    public bool LastSendQueued { get; private set; }

    // How long to wait for the server to confirm an SOS before treating it as queued offline.
    private static readonly TimeSpan SendConfirmTimeout = TimeSpan.FromSeconds(10);

    // Caps on the lookups before the write, so weak signal can't hold an SOS back: past these the
    // alert goes out without the detail (driver name falls back, no unit / no last fix).
    private static readonly TimeSpan DriverAndUnitLookupLimit = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan LastShiftFixLookupLimit = TimeSpan.FromSeconds(3);

    // A last-known fix this recent is used as it is instead of waiting for a fresh one (same as
    // the SOS button, ActiveShiftViewModel.GetSosLocationAsync).
    private static readonly TimeSpan RecentFixAge = TimeSpan.FromSeconds(30);

    // Last good copy of system_configs/global.managerPhoneNumbers, for Call Manager with no signal.
    private const string ManagerPhonesCacheKey = "ManagerPhoneNumbersCache";

    private readonly IShiftManagementService _shiftService;
    private readonly IEmergencyFeedback _feedback;

    // The taxi unit is looked up once per shift: an automated alert fires from a background
    // service, where shaving Firestore round trips off the write matters.
    private string? _cachedShiftId;
    private DriverContext? _cachedContext;

    // Driver and unit details carried on every alert. TaxiId and Phone are what SOS caller
    // validation (ManagerWeb SosPushService) checks against the unit's active shift and the
    // driver's registered number.
    private sealed record DriverContext(string DriverName, string TaxiUnit, string TaxiId, string Phone);

    public EmergencyAlertService(IShiftManagementService shiftService, IEmergencyFeedback feedback)
    {
        _shiftService = shiftService;
        _feedback = feedback;
    }

    public async Task<string?> SendAlertAsync(string triggerType, double? latitude = null, double? longitude = null, bool tryLiveFix = true)
    {
        string? shiftId = await SecureStorage.GetAsync("ActiveShiftDocumentId");
        if (string.IsNullOrWhiteSpace(shiftId))
        {
            return null;
        }

        IFirebaseUser? user = CrossFirebaseAuth.Current.CurrentUser;
        string driverId = user?.Uid ?? string.Empty;

        // Started now, alongside the location lookup below, instead of after it. Usually already
        // cached by PrepareForShiftAsync.
        Task<DriverContext> driverAndUnitLookup = GetDriverAndUnitAsync(shiftId, user);

        // An SOS is never dropped for lack of GPS: live fix, else the shift's last telemetry
        // point, else no location at all (0/0 - ManagerWeb and the Alert Center show
        // "location unavailable" for that and still dispatch on driver + unit).
        string locationSource = LocationSourceLive;
        if (latitude is null || longitude is null)
        {
            Location? location = tryLiveFix ? await TryGetLocationAsync() : null;
            if (location != null)
            {
                latitude = location.Latitude;
                longitude = location.Longitude;
            }
            else if (await WithinAsync(TryGetLastShiftFixAsync(shiftId), LastShiftFixLookupLimit, null) is (double lastLat, double lastLng))
            {
                latitude = lastLat;
                longitude = lastLng;
                locationSource = LocationSourceLastShiftFix;
            }
            else
            {
                latitude = 0;
                longitude = 0;
                locationSource = LocationSourceNone;
            }
        }
        LastLocationSource = locationSource;

        string fallbackName = string.IsNullOrWhiteSpace(user?.DisplayName) ? "Unknown Driver" : user.DisplayName;
        DriverContext context = await WithinAsync(driverAndUnitLookup, DriverAndUnitLookupLimit,
            new DriverContext(fallbackName, string.Empty, string.Empty, string.Empty));
        string driverName = context.DriverName;
        string taxiUnit = context.TaxiUnit;

        var alert = new EmergencyAlertProxy
        {
            ShiftId = shiftId,
            DriverId = driverId,
            DriverName = driverName,
            TaxiUnit = taxiUnit,
            Latitude = latitude.Value,
            Longitude = longitude.Value,
            IsResolved = false,
            Timestamp = DateTime.UtcNow,
            TriggerType = triggerType,
            LocationSource = locationSource,
            TaxiId = context.TaxiId,
            CallerPhone = context.Phone,
            DevicePhone = GetDevicePhone() ?? string.Empty,
        };

        // The document ID exists before the write, so an alert that can't reach the server yet still
        // has one. Firestore keeps an unconfirmed write in its local cache and sends it by itself
        // once the phone reconnects - so after SendConfirmTimeout the SOS counts as sent-but-queued
        // instead of leaving the driver waiting on a spinner with no signal.
        IDocumentReference doc = CrossFirebaseFirestore.Current
            .GetCollection("emergency_alerts")
            .CreateDocument();
        Task write = doc.SetDataAsync(alert);
        LastSendQueued = await Task.WhenAny(write, Task.Delay(SendConfirmTimeout)) != write;
        if (LastSendQueued)
        {
            // Observe the eventual outcome, so a later failure is logged rather than lost.
            _ = write.ContinueWith(t => System.Diagnostics.Debug.WriteLine(t.IsFaulted
                    ? $"Queued SOS {doc.Id} failed to sync: {t.Exception?.GetBaseException().Message}"
                    : $"Queued SOS {doc.Id} reached the server."),
                TaskScheduler.Default);
        }
        else
        {
            await write; // surfaces a real refusal (e.g. permission denied) as before
        }

        // Logged here so both the manual SOS button and automated detection are audited.
        // Not awaited: the alert is already saved, so the driver's confirmation doesn't wait on a second write.
        AuditLogWriter.Record("SosTriggered",
            $"Triggered {SosDispatchService.TriggerLabel(triggerType)} SOS for {taxiUnit} ({driverName}) during shift {shiftId}.");

        // Every source passes through here, so the heads-up covers the button, Hostile and Crash.
        _feedback.AlertSent(triggerType);

        return doc.Id;
    }

    public async Task PrepareForShiftAsync(string shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
        {
            return;
        }

        // GetDriverAndUnitAsync caches the result for this shift and never throws.
        await GetDriverAndUnitAsync(shiftId, CrossFirebaseAuth.Current.CurrentUser);
    }

    /// <summary>The SIM's own number when Android exposes it (best effort, never throws).</summary>
    private static string? GetDevicePhone()
    {
#if ANDROID
        return Platforms.Android.Emergency.DevicePhoneNumber.TryGet();
#else
        return null;
#endif
    }

    /// <summary><paramref name="task"/>'s result if it finishes within <paramref name="limit"/>,
    /// else <paramref name="fallback"/> (the task keeps running; for lookups that never throw).</summary>
    private static async Task<T> WithinAsync<T>(Task<T> task, TimeSpan limit, T fallback) =>
        await Task.WhenAny(task, Task.Delay(limit)) == task ? await task : fallback;

    public async Task<IReadOnlyList<string>> GetManagerPhoneNumbersAsync()
    {
        try
        {
            var config = await CrossFirebaseFirestore.Current
                .GetCollection("system_configs")
                .GetDocument("global")
                .GetDocumentSnapshotAsync<ManagerPhonesProxy>();

            List<string> numbers = (config?.Data?.ManagerPhoneNumbers ?? new List<string>())
                .Select(InputValidator.NormalizePhilippineMobile)
                .OfType<string>()
                .Distinct()
                .ToList();

            if (numbers.Count > 0)
            {
                Preferences.Set(ManagerPhonesCacheKey, string.Join(",", numbers));
            }
            return numbers;
        }
        catch (Exception ex)
        {
            // No signal (or the read failed): the last list this phone saw, so Call Manager still works.
            System.Diagnostics.Debug.WriteLine($"Manager phone lookup failed, using cached numbers: {ex.Message}");
            return Preferences.Get(ManagerPhonesCacheKey, string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }
    }

    /// <summary>The shift's last GPS point (GpsTelemetryService writes currentLatitude/Longitude
    /// on the shift doc every 30s) - null when it has none or can't be read.</summary>
    private static async Task<(double Latitude, double Longitude)?> TryGetLastShiftFixAsync(string shiftId)
    {
        try
        {
            var snapshot = await CrossFirebaseFirestore.Current
                .GetCollection("shifts")
                .GetDocument(shiftId)
                .GetDocumentSnapshotAsync<ShiftPositionProxy>();

            object? lat = snapshot?.Data?.CurrentLatitude;
            object? lng = snapshot?.Data?.CurrentLongitude;
            if (lat is null || lng is null)
            {
                return null;
            }

            // Whole-number values come back as integers (see QuickLedgerService), so convert loosely.
            double latitude = Convert.ToDouble(lat, System.Globalization.CultureInfo.InvariantCulture);
            double longitude = Convert.ToDouble(lng, System.Globalization.CultureInfo.InvariantCulture);
            return latitude == 0 && longitude == 0 ? null : (latitude, longitude);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Emergency last-fix lookup failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<Location?> TryGetLocationAsync()
    {
        // No permission prompt here: an automated alert can fire with the app in the
        // background, where there's no screen to show one on. The manual SOS button asks first.
        try
        {
            if (await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>() != PermissionStatus.Granted)
            {
                return null;
            }

            Location? lastKnown = await Geolocation.Default.GetLastKnownLocationAsync();
            if (lastKnown != null && DateTimeOffset.UtcNow - lastKnown.Timestamp <= RecentFixAge)
            {
                return lastKnown;
            }

            return await Geolocation.Default.GetLocationAsync(
                       new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(15)))
                   ?? lastKnown;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Emergency location lookup failed: {ex.Message}");
            return null;
        }
    }

    private async Task<DriverContext> GetDriverAndUnitAsync(string shiftId, IFirebaseUser? user)
    {
        if (_cachedShiftId == shiftId && _cachedContext != null)
        {
            return _cachedContext;
        }

        string driverName = string.IsNullOrWhiteSpace(user?.DisplayName) ? string.Empty : user.DisplayName;
        string taxiUnit = string.Empty;
        string resolvedTaxiId = string.Empty;
        string phone = string.Empty;

        if (user != null)
        {
            try
            {
                var profile = await CrossFirebaseFirestore.Current
                    .GetCollection("users")
                    .GetDocument(user.Uid)
                    .GetDocumentSnapshotAsync<DriverProfileProxy>();

                if (string.IsNullOrWhiteSpace(driverName))
                {
                    driverName = profile?.Data?.FullName ?? string.Empty;
                }
                // Stored as text, or as a number on some older profiles (hence object).
                phone = InputValidator.NormalizePhilippineMobile(
                    Convert.ToString(profile?.Data?.PhoneNumber, System.Globalization.CultureInfo.InvariantCulture)) ?? string.Empty;

                // Same lookup as the Active Shift screen: the unit actually being driven today
                // (a substitute, if one was assigned).
                string? taxiId = await _shiftService.GetTodaysTaxiIdAsync(profile?.Data?.AssignedTaxiId);
                if (!string.IsNullOrWhiteSpace(taxiId))
                {
                    resolvedTaxiId = taxiId;
                    var taxi = await _shiftService.GetTaxiUnitAsync(taxiId);
                    if (taxi != null)
                    {
                        taxiUnit = string.IsNullOrWhiteSpace(taxi.PlateNumber)
                            ? taxi.Model
                            : taxi.PlateNumber.Replace("-", " · ");
                    }
                }
            }
            catch (Exception ex)
            {
                // An SOS must still go out without these - the manager can see the driver ID.
                System.Diagnostics.Debug.WriteLine($"Emergency driver/unit lookup failed: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(driverName)) driverName = "Unknown Driver";

        var context = new DriverContext(driverName, taxiUnit, resolvedTaxiId, phone);
        if (!string.IsNullOrWhiteSpace(taxiUnit))
        {
            _cachedShiftId = shiftId;
            _cachedContext = context;
        }

        return context;
    }

    private class EmergencyAlertProxy
    {
        [FirestoreProperty("shiftId")]
        public string ShiftId { get; set; } = string.Empty;

        [FirestoreProperty("driverId")]
        public string DriverId { get; set; } = string.Empty;

        [FirestoreProperty("driverName")]
        public string DriverName { get; set; } = string.Empty;

        [FirestoreProperty("taxiUnit")]
        public string TaxiUnit { get; set; } = string.Empty;

        [FirestoreProperty("latitude")]
        public double Latitude { get; set; }

        [FirestoreProperty("longitude")]
        public double Longitude { get; set; }

        [FirestoreProperty("isResolved")]
        public bool IsResolved { get; set; }

        [FirestoreProperty("timestamp")]
        public DateTime Timestamp { get; set; }

        [FirestoreProperty("triggerType")]
        public string TriggerType { get; set; } = string.Empty;

        [FirestoreProperty("locationSource")]
        public string LocationSource { get; set; } = string.Empty;

        [FirestoreProperty("taxiId")]
        public string TaxiId { get; set; } = string.Empty;

        [FirestoreProperty("callerPhone")]
        public string CallerPhone { get; set; } = string.Empty;

        [FirestoreProperty("devicePhone")]
        public string DevicePhone { get; set; } = string.Empty;
    }

    private class ShiftPositionProxy
    {
        [FirestoreProperty("currentLatitude")]
        public object? CurrentLatitude { get; set; }

        [FirestoreProperty("currentLongitude")]
        public object? CurrentLongitude { get; set; }
    }

    private class DriverProfileProxy
    {
        [FirestoreProperty("fullName")]
        public string FullName { get; set; } = string.Empty;

        [FirestoreProperty("assignedTaxiId")]
        public string AssignedTaxiId { get; set; } = string.Empty;

        [FirestoreProperty("phoneNumber")]
        public object? PhoneNumber { get; set; }
    }

    private class ManagerPhonesProxy
    {
        [FirestoreProperty("managerPhoneNumbers")]
        public List<string> ManagerPhoneNumbers { get; set; } = new();
    }
}
