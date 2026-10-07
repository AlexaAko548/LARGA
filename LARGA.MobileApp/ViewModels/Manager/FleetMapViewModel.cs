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

    // Carried along purely so the page can dead-reckon the pin between real updates (project
    // it forward from this exact fix using speed/heading) instead of it sitting frozen for the
    // full ~30s gap between writes - see ManagerDashboardPage.CurrentDisplayPosition.
    public int SpeedKmh { get; set; }
    public double HeadingDegrees { get; set; }
    public DateTime PositionTimestamp { get; set; }

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

/// <summary>One always-visible "jump to this taxi" shortcut at the bottom of the map, for
/// every taxi in the fleet - not just the ones with a plotted pin. Tapping a unit with no
/// pin (no active shift / no telemetry yet) has nothing to jump to, so it just says so.</summary>
public class UnitChip : BindableObject
{
    public string TaxiId { get; set; } = string.Empty;
    public string UnitLabel { get; set; } = string.Empty;
    public FleetDriverStatus Status { get; set; }
    public bool HasPin { get; set; }

    public Color StatusColor => Status switch
    {
        FleetDriverStatus.Sos => Color.FromArgb("#D33F3F"),
        FleetDriverStatus.OnBreak => Color.FromArgb("#C97A1B"),
        FleetDriverStatus.Idle => Color.FromArgb("#6B808A"),
        _ => Color.FromArgb("#1E8E5A"),
    };

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectionStrokeThickness)); }
    }

    public double SelectionStrokeThickness => IsSelected ? 2 : 1;
}

public class FleetMapViewModel : BindableObject
{
    // Idle threshold: no telemetry update / no movement within this window counts as Idle
    // rather than Active. LAR-81: now read from system_configs/global, the same manager-
    // configurable value FleetReportingService (ManagerWeb) already uses, instead of a local
    // hardcoded guess - a manager tuning the threshold on the web should see it take effect
    // on the mobile map too.
    private const double DefaultIdleThresholdMinutes = 15;
    private double _idleThresholdMinutes = DefaultIdleThresholdMinutes;

    private readonly List<FleetPin> _allPins = new();

    public ObservableCollection<FleetPin> Pins { get; } = new();
    public ObservableCollection<UnitChip> UnitChips { get; } = new();

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
        // ApplyFilter first: the page re-renders the map on this PropertyChanged, so Pins must
        // already hold the newly-filtered set by the time it's raised.
        set { _statusFilter = value; ApplyFilter(); OnPropertyChanged(); }
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
            OnPropertyChanged(nameof(IsChipsRowVisible));
            foreach (var chip in UnitChips)
            {
                chip.IsSelected = value != null && chip.TaxiId == value.TaxiId;
            }
            if (value != null)
            {
                _ = LoadSelectedLocationAsync(value);
            }
        }
    }

    public bool IsDetailVisible => SelectedPin != null;

    /// <summary>Hide the bottom unit-shortcut row while the detail sheet is open - both
    /// anchor to the bottom of the screen and would otherwise overlap.</summary>
    public bool IsChipsRowVisible => !IsDetailVisible;

    private string _selectedLocationText = string.Empty;
    public string SelectedLocationText
    {
        get => _selectedLocationText;
        set { _selectedLocationText = value; OnPropertyChanged(); }
    }

    private static readonly TimeZoneInfo TalisayTimeZone = ResolveTalisayTimeZone();
    private readonly System.Timers.Timer _clockTimer;

    // Live Firestore listeners instead of a client-side poll timer: Firestore pushes a new
    // snapshot the instant a driver's phone writes (no extra ~30s of poll-cycle lag stacked on
    // top of the write cadence), and it's billed per changed document rather than re-reading
    // everything on a schedule - cheaper than the polling this replaced, not more expensive.
    // Start/Stop are called from the page's OnAppearing/OnDisappearing so this doesn't keep
    // reading Firestore while the manager is elsewhere in the app.
    private IDisposable? _shiftsListener;
    private IDisposable? _sosListener;
    private Dictionary<string, ShiftProxy> _activeShiftsById = new();
    private HashSet<string> _sosShiftIds = new();

    // Driver/taxi lookups rarely change, so these persist across rebuilds (cleared on each
    // StartListening) instead of being refetched every time any single shift's position
    // updates - a snapshot listener can fire far more often than the old 30s poll did.
    private readonly Dictionary<string, DriverLookup> _driverCache = new();
    private readonly Dictionary<string, TaxiLookup> _taxiCache = new();
    private List<string> _allTaxiIds = new();

    // Bumped at the start of every RebuildPinsAsync. Both listeners (plus the static-data load)
    // can start overlapping rebuilds, and one that awaits a cold driver/taxi lookup can finish
    // after a newer one - only the latest-started rebuild is allowed to publish its results, so
    // an older snapshot (e.g. from before an SOS arrived) can never overwrite a newer one.
    private int _rebuildVersion;

    private string _currentDateText = string.Empty;
    public string CurrentDateText
    {
        get => _currentDateText;
        set { _currentDateText = value; OnPropertyChanged(); }
    }

    // Fires once per LoadFleetAsync run, after Pins/UnitChips are fully rebuilt - unlike
    // Pins.CollectionChanged (which fires once per Clear() and once per Add(), each
    // individually, since ApplyFilter rebuilds the collection item-by-item), this is safe for
    // code that needs the complete, settled pin list, e.g. Alert Center's "jump to this
    // driver on the map" handoff (see MapFocusRequest).
    public event EventHandler? FleetLoaded;

    // Raised by NavigateCommand - the page (which owns the actual MapControl/Navigator)
    // zooms in tight on this pin's exact coordinates in response.
    public event EventHandler<FleetPin>? NavigateRequested;

    public ICommand SelectPinCommand { get; }
    public ICommand SelectUnitCommand { get; }
    public ICommand CloseDetailCommand { get; }
    public ICommand ToggleFilterCommand { get; }
    public ICommand CallCommand { get; }
    public ICommand MessageCommand { get; }
    public ICommand NavigateCommand { get; }

    public FleetMapViewModel()
    {
        SelectPinCommand = new Command<FleetPin>(pin => SelectedPin = pin);
        SelectUnitCommand = new Command<UnitChip>(async chip =>
        {
            var pin = _allPins.FirstOrDefault(p => p.TaxiId == chip.TaxiId);
            if (pin != null)
            {
                SelectedPin = pin;
            }
            else
            {
                await Shell.Current.DisplayAlert(chip.UnitLabel, "This unit has no live location right now - it isn't on an active shift.", "OK");
            }
        });
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

        NavigateCommand = new Command(() =>
        {
            // Zoom in tight on the driver's exact position on the app's own Live Fleet map,
            // rather than handing off to an external maps app - same reasoning as the Alert
            // Center car icon fix.
            if (SelectedPin == null) return;
            NavigateRequested?.Invoke(this, SelectedPin);
        });

        UpdateCurrentDate();
        _clockTimer = new System.Timers.Timer(60_000);
        _clockTimer.Elapsed += (_, _) => MainThread.BeginInvokeOnMainThread(UpdateCurrentDate);
        _clockTimer.Start();
    }

    public void StartListening()
    {
        if (_shiftsListener != null) return; // already listening

        _driverCache.Clear();
        _taxiCache.Clear();
        _ = InitializeStaticDataAsync();

        _shiftsListener = CrossFirebaseFirestore.Current
            .GetCollection("shifts")
            .WhereEqualsTo("status", "Active")
            .AddSnapshotListener<ShiftProxy>(snapshot =>
            {
                _activeShiftsById = snapshot.Documents
                    .Where(d => d.Data != null)
                    .ToDictionary(d => d.Reference.Id, d => d.Data!);
                _ = RebuildPinsAsync();
            });

        _sosListener = CrossFirebaseFirestore.Current
            .GetCollection("emergency_alerts")
            .WhereEqualsTo("isResolved", false)
            .AddSnapshotListener<SosProxy>(snapshot =>
            {
                _sosShiftIds = snapshot.Documents
                    .Where(d => d.Data != null)
                    .Select(d => d.Data!.ShiftId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet();
                _ = RebuildPinsAsync();
            });
    }

    public void StopListening()
    {
        _shiftsListener?.Dispose();
        _shiftsListener = null;
        _sosListener?.Dispose();
        _sosListener = null;
    }

    private async Task InitializeStaticDataAsync()
    {
        _idleThresholdMinutes = await GetIdleThresholdMinutesAsync();

        try
        {
            // Unit shortcuts show every taxi in the fleet, not just the ones with a pin - a
            // manager should be able to jump to (or find out there's nothing to jump to for)
            // any unit from this same row. The roster itself changes rarely, so this is a
            // one-shot fetch per tab-visit rather than something the live listeners need to
            // track.
            var allTaxisSnapshot = await CrossFirebaseFirestore.Current
                .GetCollection("taxis")
                .GetDocumentsAsync<TaxiLookup>();

            _allTaxiIds = allTaxisSnapshot.Documents
                .Where(d => d.Data != null)
                .Select(d => d.Reference.Id)
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Taxi roster fetch failed: {ex.Message}");
        }

        // The listeners' first snapshot can land (often instantly, from Firestore's local
        // cache) before the roster/threshold above - rebuild once more now that they're in, or
        // the unit chips row stays empty until some driver's next write fires a listener.
        await RebuildPinsAsync();
    }

    private void UpdateCurrentDate()
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TalisayTimeZone);
        CurrentDateText = localNow.ToString("dddd, d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Talisay, Cebu follows Philippine Time (UTC+8, no DST). "Asia/Manila" is the
    /// IANA id Android resolves; "Singapore Standard Time" is the Windows id, kept as a
    /// fallback for local/dev runs off-device. A fixed UTC+8 offset is the last resort so the
    /// date label never breaks even if neither id is available.</summary>
    private static TimeZoneInfo ResolveTalisayTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila"); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        try { return TimeZoneInfo.FindSystemTimeZoneById("Singapore Standard Time"); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        return TimeZoneInfo.CreateCustomTimeZone("PHT", TimeSpan.FromHours(8), "Philippine Time", "PHT");
    }

    /// <summary>Recomputes Pins/UnitChips/stat counts from the two listeners' latest cached
    /// snapshots (_activeShiftsById, _sosShiftIds). Runs every time either listener fires -
    /// which, with a live fleet, can be far more often than the old 30s poll (any single
    /// driver's write re-delivers the whole "shifts" snapshot) - so driver/taxi lookups use
    /// the persistent instance caches above rather than refetching every call.</summary>
    private async Task RebuildPinsAsync()
    {
        try
        {
            // Snapshot the dictionaries so a listener firing again mid-rebuild (e.g. two
            // drivers' writes land close together) can't mutate the collection out from under
            // the loop below.
            var activeShifts = _activeShiftsById;
            var sosShiftIds = _sosShiftIds;
            var version = Interlocked.Increment(ref _rebuildVersion);

            // Tallied locally and only published at the end - incrementing the bound properties
            // directly across the awaits below let two overlapping rebuilds both add onto the
            // same counts (double-counted stat pills on every Map tab visit).
            int active = 0, onBreak = 0, idle = 0, sos = 0;

            var pins = new List<FleetPin>();
            var now = DateTime.UtcNow;

            foreach (var (shiftId, data) in activeShifts)
            {
                var driver = await GetDriverAsync(data.DriverId);
                var taxi = await GetTaxiAsync(data.TaxiId);

                bool hasSos = sosShiftIds.Contains(shiftId);

                if (data.CurrentPositionUpdatedAt == default)
                {
                    // No known position yet (shift just started, first telemetry tick hasn't
                    // landed) - still counts toward the stat pills, just has no pin.
                    Tally(hasSos ? FleetDriverStatus.Sos : data.IsOnBreak ? FleetDriverStatus.OnBreak : FleetDriverStatus.Idle);
                    continue;
                }

                var positionTimestamp = FirestoreDateTimeFix.Apply(data.CurrentPositionUpdatedAt);
                bool recentlyMoving = data.CurrentSpeed > 0 && positionTimestamp >= now.AddMinutes(-_idleThresholdMinutes);

                FleetDriverStatus status = hasSos ? FleetDriverStatus.Sos
                    : data.IsOnBreak ? FleetDriverStatus.OnBreak
                    : recentlyMoving ? FleetDriverStatus.Active
                    : FleetDriverStatus.Idle;

                Tally(status);

                pins.Add(new FleetPin
                {
                    ShiftDocId = shiftId,
                    DriverId = data.DriverId,
                    DriverName = string.IsNullOrWhiteSpace(driver?.FullName) ? "Unknown Driver" : driver!.FullName,
                    PhoneNumber = driver?.PhoneNumber?.ToString(),
                    PlateNumber = string.IsNullOrWhiteSpace(taxi?.PlateNumber) ? "—" : taxi!.PlateNumber,
                    UnitDetails = taxi != null ? $"{taxi.YearManufactured} {taxi.Model}".Trim() : string.Empty,
                    TaxiId = data.TaxiId,
                    Latitude = data.CurrentLatitude,
                    Longitude = data.CurrentLongitude,
                    SpeedKmh = data.CurrentSpeed,
                    HeadingDegrees = data.CurrentHeading,
                    PositionTimestamp = positionTimestamp,
                    Status = status,
                });
            }

            // A newer rebuild started while this one was awaiting lookups - its snapshot is
            // fresher, so drop this one rather than overwrite it with stale data.
            if (version != _rebuildVersion) return;

            ActiveCount = active;
            OnBreakCount = onBreak;
            IdleCount = idle;
            SosCount = sos;

            _allPins.Clear();
            _allPins.AddRange(pins);
            ApplyFilter();

            var chips = _allTaxiIds
                .Select(taxiId =>
                {
                    var matchingPin = pins.FirstOrDefault(p => p.TaxiId == taxiId);
                    var digits = new string(taxiId.Where(char.IsDigit).ToArray());
                    var unitNumber = int.TryParse(digits, out var n) ? n : 0;
                    return new UnitChip
                    {
                        TaxiId = taxiId,
                        UnitLabel = unitNumber > 0 ? $"{unitNumber:D2}" : "—",
                        Status = matchingPin?.Status ?? FleetDriverStatus.Idle,
                        HasPin = matchingPin != null,
                    };
                })
                .OrderBy(c => c.UnitLabel)
                .ToList();

            UnitChips.Clear();
            foreach (var chip in chips)
            {
                chip.IsSelected = SelectedPin != null && chip.TaxiId == SelectedPin.TaxiId;
                UnitChips.Add(chip);
            }

            FleetLoaded?.Invoke(this, EventArgs.Empty);

            void Tally(FleetDriverStatus s)
            {
                switch (s)
                {
                    case FleetDriverStatus.Sos: sos++; break;
                    case FleetDriverStatus.OnBreak: onBreak++; break;
                    case FleetDriverStatus.Active: active++; break;
                    default: idle++; break;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Rebuild Pins Error: {ex.Message}");
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

    /// <summary>Mirrors FleetReportingService.GetIdleThresholdMinutesAsync (ManagerWeb) so a
    /// manager-configured threshold applies consistently on both surfaces. Missing doc, zero/
    /// negative value, or any read error all fall back to the same 15-minute default as the
    /// web side.</summary>
    private static async Task<double> GetIdleThresholdMinutesAsync()
    {
        try
        {
            var doc = await CrossFirebaseFirestore.Current
                .GetCollection("system_configs")
                .GetDocument("global")
                .GetDocumentSnapshotAsync<SystemConfigProxy>();

            double configured = doc?.Data?.IdleThresholdMinutes ?? 0;
            return configured > 0 ? configured : DefaultIdleThresholdMinutes;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Idle threshold config read failed: {ex.Message}");
            return DefaultIdleThresholdMinutes;
        }
    }

    private async Task<DriverLookup?> GetDriverAsync(string driverId)
    {
        if (string.IsNullOrWhiteSpace(driverId)) return null;
        if (_driverCache.TryGetValue(driverId, out var cached)) return cached;

        var doc = await CrossFirebaseFirestore.Current
            .GetCollection("users")
            .GetDocument(driverId)
            .GetDocumentSnapshotAsync<DriverLookup>();
        if (doc?.Data == null) return null;

        _driverCache[driverId] = doc.Data;
        return doc.Data;
    }

    private async Task<TaxiLookup?> GetTaxiAsync(string taxiId)
    {
        if (string.IsNullOrWhiteSpace(taxiId)) return null;
        if (_taxiCache.TryGetValue(taxiId, out var cached)) return cached;

        var doc = await CrossFirebaseFirestore.Current
            .GetCollection("taxis")
            .GetDocument(taxiId)
            .GetDocumentSnapshotAsync<TaxiLookup>();
        if (doc?.Data == null) return null;

        _taxiCache[taxiId] = doc.Data;
        return doc.Data;
    }

    private class ShiftProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("driverId")]
        public string DriverId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("taxiId")]
        public string TaxiId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("isOnBreak")]
        public bool IsOnBreak { get; set; }

        // Denormalized by GpsTelemetryService (TelemetryServices.cs) onto the shift doc
        // itself each tick, so the live "shifts" listener alone is enough to plot the pin -
        // no separate gps_telemetry query/listener per shift needed. Null until the first
        // telemetry tick lands after clock-in.
        [Plugin.Firebase.Firestore.FirestoreProperty("currentLatitude")]
        public double CurrentLatitude { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("currentLongitude")]
        public double CurrentLongitude { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("currentSpeed")]
        public int CurrentSpeed { get; set; }

        // Negative (-1) when the driver's phone reported no course for that fix.
        [Plugin.Firebase.Firestore.FirestoreProperty("currentHeading")]
        public double CurrentHeading { get; set; }

        // Plugin.Firebase.Firestore's Android deserializer only handles plain DateTime, not
        // Nullable<DateTime> (it crashes converting the native DateTimeOffset into one) - so
        // "no position yet" is signaled by this sitting at its C# default (missing fields are
        // simply left untouched by the deserializer) rather than by nullability.
        [Plugin.Firebase.Firestore.FirestoreProperty("currentPositionUpdatedAt")]
        public DateTime CurrentPositionUpdatedAt { get; set; }
    }

    private class SosProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")]
        public string ShiftId { get; set; } = string.Empty;
    }

    private class SystemConfigProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("idleThresholdMinutes")]
        public double IdleThresholdMinutes { get; set; }
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
}
