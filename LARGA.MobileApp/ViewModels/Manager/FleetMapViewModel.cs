using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.MobileApp.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.Communication;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Graphics;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.ViewModels.Manager;

public enum FleetDriverStatus { Active, OnBreak, Idle, Sos }

/// <summary>One currently-active shift plotted on the Live Fleet map. Only shifts with at
/// least one gps_telemetry reading get a pin - a driver with no reported position has
/// nowhere correct to plot - but every Active shift still counts toward the stat pills
/// regardless of whether it has a pin.</summary>
public class FleetPin
{
    public string ShiftDocId { get; set; } = string.Empty;
    public string DriverId { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string PlateNumber { get; set; } = string.Empty;
    public string UnitDetails { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public FleetDriverStatus Status { get; set; }

    /// <summary>"Unit 01" style callsign derived from the taxi's fixed ID (e.g. "TAXI_001"),
    /// since there's no separate unit-number field on the taxi record.</summary>
    public string UnitLabel
    {
        get
        {
            var digits = new string(TaxiId.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) ? $"Unit {n:D2}" : "Unit —";
        }
    }

    public string StatusLabel => Status switch
    {
        FleetDriverStatus.Sos => "SOS",
        FleetDriverStatus.OnBreak => "ON BREAK",
        FleetDriverStatus.Idle => "IDLE",
        _ => "ACTIVE",
    };

    public Color StatusColor => Status switch
    {
        FleetDriverStatus.Sos => Color.FromArgb("#D33F3F"),
        FleetDriverStatus.OnBreak => Color.FromArgb("#C97A1B"),
        FleetDriverStatus.Idle => Color.FromArgb("#6B808A"),
        _ => Color.FromArgb("#1E8E5A"),
    };

    public Color StatusBgColor => Status switch
    {
        FleetDriverStatus.Sos => Color.FromArgb("#FBEAEA"),
        FleetDriverStatus.OnBreak => Color.FromArgb("#FBF0E0"),
        FleetDriverStatus.Idle => Color.FromArgb("#EEF2F4"),
        _ => Color.FromArgb("#E3F5EC"),
    };

    public bool IsSos => Status == FleetDriverStatus.Sos;

    /// <summary>Call button accent - green for an Active unit (easy to reach, on the road),
    /// the usual blue for every other status.</summary>
    public Color CallAccentColor => Status == FleetDriverStatus.Active ? StatusColor : Color.FromArgb("#019BCF");
}

public class FleetMapViewModel : BindableObject
{
    // Idle threshold: no telemetry update / no movement within this window counts as Idle
    // rather than Active. A placeholder judgment call, same as FuelReviewDetail's tolerance
    // constants - the real number belongs in system_configs once there's real fleet data to
    // tune it against (see FleetReportingService's own idle-threshold config for the
    // ManagerWeb-side equivalent).
    private const int IdleThresholdMinutes = 10;

    private readonly List<FleetPin> _allPins = new();

    public ObservableCollection<FleetPin> Pins { get; } = new();

    private int _activeCount;
    public int ActiveCount { get => _activeCount; set { _activeCount = value; OnPropertyChanged(); } }

    private int _onBreakCount;
    public int OnBreakCount { get => _onBreakCount; set { _onBreakCount = value; OnPropertyChanged(); } }

    private int _idleCount;
    public int IdleCount { get => _idleCount; set { _idleCount = value; OnPropertyChanged(); } }

    private int _sosCount;
    public int SosCount { get => _sosCount; set { _sosCount = value; OnPropertyChanged(); } }

    private FleetDriverStatus? _statusFilter;
    public FleetDriverStatus? StatusFilter
    {
        get => _statusFilter;
        set { _statusFilter = value; OnPropertyChanged(); ApplyFilter(); }
    }

    private FleetPin? _selectedPin;
    public FleetPin? SelectedPin
    {
        get => _selectedPin;
        set
        {
            _selectedPin = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDetailVisible));
            if (value != null)
            {
                _ = LoadSelectedLocationAsync(value);
            }
        }
    }

    public bool IsDetailVisible => SelectedPin != null;

    private string _selectedLocationText = string.Empty;
    public string SelectedLocationText
    {
        get => _selectedLocationText;
        set { _selectedLocationText = value; OnPropertyChanged(); }
    }

    public ICommand LoadFleetCommand { get; }
    public ICommand SelectPinCommand { get; }
    public ICommand CloseDetailCommand { get; }
    public ICommand ToggleFilterCommand { get; }
    public ICommand CallCommand { get; }
    public ICommand MessageCommand { get; }
    public ICommand NavigateCommand { get; }

    public FleetMapViewModel()
    {
        LoadFleetCommand = new Command(async () => await LoadFleetAsync());
        SelectPinCommand = new Command<FleetPin>(pin => SelectedPin = pin);
        CloseDetailCommand = new Command(() => SelectedPin = null);
        ToggleFilterCommand = new Command<FleetDriverStatus>(status =>
            StatusFilter = StatusFilter == status ? null : status);

        CallCommand = new Command(() =>
        {
            if (SelectedPin == null || string.IsNullOrWhiteSpace(SelectedPin.PhoneNumber)) return;
            if (PhoneDialer.Default.IsSupported)
            {
                PhoneDialer.Default.Open(SelectedPin.PhoneNumber);
            }
        });

        MessageCommand = new Command(async () =>
        {
            // Same gap as Alert Center's Message action - no manager chat inbox yet.
            await Shell.Current.DisplayAlert("Not Available Yet", "Manager messaging is coming soon.", "OK");
        });

        NavigateCommand = new Command(async () =>
        {
            if (SelectedPin == null) return;
            try
            {
                var location = new Location(SelectedPin.Latitude, SelectedPin.Longitude);
                await Map.OpenAsync(location, new MapLaunchOptions { Name = SelectedPin.DriverName });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Open Map Error: {ex.Message}");
            }
        });
    }

    private async Task LoadFleetAsync()
    {
        try
        {
            var driverCache = new Dictionary<string, DriverLookup>();
            var taxiCache = new Dictionary<string, TaxiLookup>();

            var shiftsSnapshot = await CrossFirebaseFirestore.Current
                .GetCollection("shifts")
                .WhereEqualsTo("status", "Active")
                .GetDocumentsAsync<ShiftProxy>();

            var sosSnapshot = await CrossFirebaseFirestore.Current
                .GetCollection("emergency_alerts")
                .WhereEqualsTo("isResolved", false)
                .GetDocumentsAsync<SosProxy>();
            var sosShiftIds = sosSnapshot.Documents
                .Where(d => d.Data != null)
                .Select(d => d.Data.ShiftId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet();

            var pins = new List<FleetPin>();
            var now = DateTime.UtcNow;

            foreach (var doc in shiftsSnapshot.Documents)
            {
                if (doc.Data == null) continue;
                var shiftId = doc.Reference.Id;

                var driver = await GetDriverAsync(doc.Data.DriverId, driverCache);
                var taxi = await GetTaxiAsync(doc.Data.TaxiId, taxiCache);
                var latest = await GetLatestTelemetryAsync(shiftId);

                bool hasSos = sosShiftIds.Contains(shiftId);

                if (latest == null)
                {
                    // No known position - still counts toward the stat pills, just has no pin.
                    Tally(hasSos ? FleetDriverStatus.Sos : doc.Data.IsOnBreak ? FleetDriverStatus.OnBreak : FleetDriverStatus.Idle);
                    continue;
                }

                var latestTimestamp = FirestoreDateTimeFix.Apply(latest.Timestamp);
                bool recentlyMoving = latest.Speed > 0 && latestTimestamp >= now.AddMinutes(-IdleThresholdMinutes);

                FleetDriverStatus status = hasSos ? FleetDriverStatus.Sos
                    : doc.Data.IsOnBreak ? FleetDriverStatus.OnBreak
                    : recentlyMoving ? FleetDriverStatus.Active
                    : FleetDriverStatus.Idle;

                Tally(status);

                pins.Add(new FleetPin
                {
                    ShiftDocId = shiftId,
                    DriverId = doc.Data.DriverId,
                    DriverName = string.IsNullOrWhiteSpace(driver?.FullName) ? "Unknown Driver" : driver!.FullName,
                    PhoneNumber = driver?.PhoneNumber?.ToString(),
                    PlateNumber = string.IsNullOrWhiteSpace(taxi?.PlateNumber) ? "—" : taxi!.PlateNumber,
                    UnitDetails = taxi != null ? $"{taxi.YearManufactured} {taxi.Model}".Trim() : string.Empty,
                    TaxiId = doc.Data.TaxiId,
                    Latitude = latest.Latitude,
                    Longitude = latest.Longitude,
                    Status = status,
                });
            }

            _allPins.Clear();
            _allPins.AddRange(pins);
            ApplyFilter();

            void Tally(FleetDriverStatus s)
            {
                switch (s)
                {
                    case FleetDriverStatus.Sos: SosCount++; break;
                    case FleetDriverStatus.OnBreak: OnBreakCount++; break;
                    case FleetDriverStatus.Active: ActiveCount++; break;
                    default: IdleCount++; break;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Load Fleet Error: {ex.Message}");
        }
    }

    private void ApplyFilter()
    {
        var visible = StatusFilter.HasValue
            ? _allPins.Where(p => p.Status == StatusFilter.Value)
            : _allPins;

        Pins.Clear();
        foreach (var pin in visible)
        {
            Pins.Add(pin);
        }
    }

    private async Task LoadSelectedLocationAsync(FleetPin pin)
    {
        SelectedLocationText = "Locating…";
        try
        {
            var placemarks = await Geocoding.Default.GetPlacemarksAsync(pin.Latitude, pin.Longitude);
            var place = placemarks?.FirstOrDefault();
            if (place == null)
            {
                SelectedLocationText = $"{pin.Latitude:F5}, {pin.Longitude:F5}";
                return;
            }

            var parts = new[] { place.FeatureName, place.Thoroughfare, place.Locality }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct()
                .Take(2);
            var text = string.Join(", ", parts);
            SelectedLocationText = string.IsNullOrWhiteSpace(text) ? $"{pin.Latitude:F5}, {pin.Longitude:F5}" : text;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Reverse Geocode Error: {ex.Message}");
            SelectedLocationText = $"{pin.Latitude:F5}, {pin.Longitude:F5}";
        }
    }

    private async Task<DriverLookup?> GetDriverAsync(string driverId, Dictionary<string, DriverLookup> cache)
    {
        if (string.IsNullOrWhiteSpace(driverId)) return null;
        if (cache.TryGetValue(driverId, out var cached)) return cached;

        var doc = await CrossFirebaseFirestore.Current
            .GetCollection("users")
            .GetDocument(driverId)
            .GetDocumentSnapshotAsync<DriverLookup>();
        if (doc?.Data == null) return null;

        cache[driverId] = doc.Data;
        return doc.Data;
    }

    private async Task<TaxiLookup?> GetTaxiAsync(string taxiId, Dictionary<string, TaxiLookup> cache)
    {
        if (string.IsNullOrWhiteSpace(taxiId)) return null;
        if (cache.TryGetValue(taxiId, out var cached)) return cached;

        var doc = await CrossFirebaseFirestore.Current
            .GetCollection("taxis")
            .GetDocument(taxiId)
            .GetDocumentSnapshotAsync<TaxiLookup>();
        if (doc?.Data == null) return null;

        cache[taxiId] = doc.Data;
        return doc.Data;
    }

    private async Task<TelemetryPoint?> GetLatestTelemetryAsync(string shiftId)
    {
        try
        {
            var snapshot = await CrossFirebaseFirestore.Current
                .GetCollection("gps_telemetry")
                .WhereEqualsTo("shiftId", shiftId)
                .OrderBy("timestamp", true)
                .LimitedTo(1)
                .GetDocumentsAsync<TelemetryPoint>();

            return snapshot.Documents.FirstOrDefault()?.Data;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Telemetry Query Error ({shiftId}): {ex.Message}");
            return null;
        }
    }

    private class ShiftProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("driverId")]
        public string DriverId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")]
        public string TaxiId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("isOnBreak")]
        public bool IsOnBreak { get; set; }
    }

    private class SosProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")]
        public string ShiftId { get; set; } = string.Empty;
    }

    private class DriverLookup
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("fullName")]
        public string FullName { get; set; } = string.Empty;

        // object, not string - see AlertCenterViewModel's DriverLookup for why.
        [Plugin.Firebase.Firestore.FirestoreProperty("phoneNumber")]
        public object? PhoneNumber { get; set; }
    }

    private class TaxiLookup
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("plateNumber")]
        public string PlateNumber { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("model")]
        public string Model { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("yearManufactured")]
        public int YearManufactured { get; set; }
    }

    private class TelemetryPoint
    {
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
