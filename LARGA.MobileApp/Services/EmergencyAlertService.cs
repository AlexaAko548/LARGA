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
    /// location is looked up. Returns the new document's ID, or null when there's no active
    /// shift or no location could be found.
    /// </summary>
    Task<string?> SendAlertAsync(string triggerType, double? latitude = null, double? longitude = null);

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
    private readonly IShiftManagementService _shiftService;
    private readonly IEmergencyFeedback _feedback;

    // The taxi unit is looked up once per shift: an automated alert fires from a background
    // service, where shaving Firestore round trips off the write matters.
    private string? _cachedShiftId;
    private string? _cachedTaxiUnit;
    private string? _cachedDriverName;

    public EmergencyAlertService(IShiftManagementService shiftService, IEmergencyFeedback feedback)
    {
        _shiftService = shiftService;
        _feedback = feedback;
    }

    public async Task<string?> SendAlertAsync(string triggerType, double? latitude = null, double? longitude = null)
    {
        string? shiftId = await SecureStorage.GetAsync("ActiveShiftDocumentId");
        if (string.IsNullOrWhiteSpace(shiftId))
        {
            return null;
        }

        IFirebaseUser? user = CrossFirebaseAuth.Current.CurrentUser;
        string driverId = user?.Uid ?? string.Empty;

        if (latitude is null || longitude is null)
        {
            Location? location = await TryGetLocationAsync();
            if (location == null)
            {
                return null;
            }
            latitude = location.Latitude;
            longitude = location.Longitude;
        }

        (string driverName, string taxiUnit) = await GetDriverAndUnitAsync(shiftId, user);

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
        };

        IDocumentReference doc = await CrossFirebaseFirestore.Current
            .GetCollection("emergency_alerts")
            .AddDocumentAsync(alert);

        // Logged here so both the manual SOS button and automated detection are audited.
        await AuditLogWriter.WriteAsync("SosTriggered",
            $"Triggered {SosDispatchService.TriggerLabel(triggerType)} SOS for {taxiUnit} ({driverName}) during shift {shiftId}.");

        // Every source passes through here, so the heads-up covers the button, Hostile and Crash.
        _feedback.AlertSent(triggerType);

        return doc.Id;
    }

    public async Task<IReadOnlyList<string>> GetManagerPhoneNumbersAsync()
    {
        try
        {
            var config = await CrossFirebaseFirestore.Current
                .GetCollection("system_configs")
                .GetDocument("global")
                .GetDocumentSnapshotAsync<ManagerPhonesProxy>();

            return (config?.Data?.ManagerPhoneNumbers ?? new List<string>())
                .Select(InputValidator.NormalizePhilippineMobile)
                .OfType<string>()
                .Distinct()
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Manager phone lookup failed: {ex.Message}");
            return Array.Empty<string>();
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

            return await Geolocation.Default.GetLocationAsync(
                       new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(15)))
                   ?? await Geolocation.Default.GetLastKnownLocationAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Emergency location lookup failed: {ex.Message}");
            return null;
        }
    }

    private async Task<(string DriverName, string TaxiUnit)> GetDriverAndUnitAsync(string shiftId, IFirebaseUser? user)
    {
        if (_cachedShiftId == shiftId && _cachedTaxiUnit != null && _cachedDriverName != null)
        {
            return (_cachedDriverName, _cachedTaxiUnit);
        }

        string driverName = string.IsNullOrWhiteSpace(user?.DisplayName) ? string.Empty : user.DisplayName;
        string taxiUnit = string.Empty;

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

                // Same lookup as the Active Shift screen: the unit actually being driven today
                // (a substitute, if one was assigned).
                string? taxiId = await _shiftService.GetTodaysTaxiIdAsync(profile?.Data?.AssignedTaxiId);
                if (!string.IsNullOrWhiteSpace(taxiId))
                {
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

        if (!string.IsNullOrWhiteSpace(taxiUnit))
        {
            _cachedShiftId = shiftId;
            _cachedTaxiUnit = taxiUnit;
            _cachedDriverName = driverName;
        }

        return (driverName, taxiUnit);
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
    }

    private class DriverProfileProxy
    {
        [FirestoreProperty("fullName")]
        public string FullName { get; set; } = string.Empty;

        [FirestoreProperty("assignedTaxiId")]
        public string AssignedTaxiId { get; set; } = string.Empty;
    }

    private class ManagerPhonesProxy
    {
        [FirestoreProperty("managerPhoneNumbers")]
        public List<string> ManagerPhoneNumbers { get; set; } = new();
    }
}
