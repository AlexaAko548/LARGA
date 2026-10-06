using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using LARGA.SharedCore;
using LARGA.SharedCore.Services;
using LARGA.Shared.Models.Entities;
using LARGA.MobileApp.Services;

namespace LARGA.MobileApp.ViewModels.Driver;

public class ActiveShiftViewModel : INotifyPropertyChanged, IQueryAttributable
{
    private readonly IShiftManagementService _shiftService;
    private readonly IEmergencyAlertService _emergencyAlertService;
    private readonly IDispatcherTimer _shiftTimer;
    private TimeSpan _shiftDuration;
    private TimeSpan _timeRemaining;
    private DateTime _shiftStartTime;

    private DateTime _pauseStartTime;
    private TimeSpan _totalBreakTime = TimeSpan.Zero;

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            _isPaused = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(StatusBannerText));
            OnPropertyChanged(nameof(StatusBannerColor));
            OnPropertyChanged(nameof(ShiftStatus));
        }
    }
    public bool IsActive => !IsPaused;

    private bool _isSosAlertVisible;
    public bool IsSosAlertVisible
    {
        get => _isSosAlertVisible;
        set { _isSosAlertVisible = value; OnPropertyChanged(); }
    }

    private bool _isClockOutAlertVisible;
    public bool IsClockOutAlertVisible
    {
        get => _isClockOutAlertVisible;
        set { _isClockOutAlertVisible = value; OnPropertyChanged(); }
    }

    private bool _isPauseAlertVisible;
    public bool IsPauseAlertVisible
    {
        get => _isPauseAlertVisible;
        set { _isPauseAlertVisible = value; OnPropertyChanged(); }
    }

    public string StatusBannerText => IsPaused ? "On Break - GPS Tracking On" : "Active Shift - GPS Tracking On";
    public Color StatusBannerColor => IsPaused ? Colors.Yellow : Colors.Lime;
    public string ShiftStatus => IsPaused ? "Shift Paused." : "On the Road.";

    private string _taxiUnit = "Loading...";
    public string TaxiUnit
    {
        get => _taxiUnit;
        set { _taxiUnit = value; OnPropertyChanged(); }
    }

    private string _shiftStartTimeDisplay = string.Empty;
    public string ShiftStartTimeDisplay
    {
        get => _shiftStartTimeDisplay;
        set { _shiftStartTimeDisplay = value; OnPropertyChanged(); }
    }

    private string _shiftEndsAt = string.Empty;
    public string ShiftEndsAt
    {
        get => _shiftEndsAt;
        set { _shiftEndsAt = value; OnPropertyChanged(); }
    }

    public string Distance { get; set; } = "0";
    public string BoundaryStatus { get; set; } = "Pending";

    private string _durationDisplay = "00:00:00";
    public string DurationDisplay
    {
        get => _durationDisplay;
        set { _durationDisplay = value; OnPropertyChanged(); }
    }

    private string _timeRemainingDisplay = string.Empty;
    public string TimeRemainingDisplay
    {
        get => _timeRemainingDisplay;
        set { _timeRemainingDisplay = value; OnPropertyChanged(); }
    }

    public ICommand PauseShiftCommand { get; }
    public ICommand ResumeShiftCommand { get; }
    public ICommand ClockOutCommand { get; }
    public ICommand ConfirmClockOutCommand { get; }
    public ICommand CancelClockOutCommand { get; }
    public ICommand SendSosCommand { get; }
    public ICommand DismissSosCommand { get; }
    public ICommand RequestPauseCommand { get; }
    public ICommand ConfirmPauseCommand { get; }
    public ICommand CancelPauseCommand { get; }

    public ActiveShiftViewModel(IShiftManagementService shiftService, IEmergencyAlertService emergencyAlertService)
    {
        _shiftService = shiftService;
        _emergencyAlertService = emergencyAlertService;

        // Timer instantiation remains in the constructor so it exists globally
        _shiftTimer = Application.Current.Dispatcher.CreateTimer();
        _shiftTimer.Interval = TimeSpan.FromSeconds(1);
        _shiftTimer.Tick += OnTimerTick;

        RequestPauseCommand = new Command(() => IsPauseAlertVisible = true);
        CancelPauseCommand = new Command(() => IsPauseAlertVisible = false);
        ConfirmPauseCommand = new Command(() =>
        {
            IsPauseAlertVisible = false;
            IsPaused = true;
            _pauseStartTime = ShiftClock.LocalNow;
            _shiftTimer.Stop();
            _ = SyncBreakStatusAsync(true);
        });

        ResumeShiftCommand = new Command(() =>
        {
            IsPaused = false;
            _totalBreakTime += (ShiftClock.LocalNow - _pauseStartTime);
            _shiftTimer.Start();
            _ = SyncBreakStatusAsync(false);
        });

        ClockOutCommand = new Command(() => IsClockOutAlertVisible = true);
        CancelClockOutCommand = new Command(() => IsClockOutAlertVisible = false);

        ConfirmClockOutCommand = new Command(async () =>
        {
            IsClockOutAlertVisible = false;
            await Shell.Current.GoToAsync("end-shift-step1");
        });

        SendSosCommand = new Command(async () => await SendSosAsync());
        DismissSosCommand = new Command(() => IsSosAlertVisible = false);
    }

    private bool _isSendingSos;

    private async Task SendSosAsync()
    {
        if (_isSendingSos) return;
        _isSendingSos = true;

        try
        {
            string shiftId = await SecureStorage.GetAsync("ActiveShiftDocumentId");
            if (string.IsNullOrWhiteSpace(shiftId))
            {
                await Shell.Current.DisplayAlert("SOS Failed", "No active shift found. Please clock in first.", "OK");
                return;
            }

            PermissionStatus status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            }

            Location? location = null;
            if (status == PermissionStatus.Granted)
            {
                location = await Geolocation.Default.GetLocationAsync(
                    new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(15)));
                location ??= await Geolocation.Default.GetLastKnownLocationAsync();
            }

            if (location == null)
            {
                await Shell.Current.DisplayAlert("SOS Failed", "Unable to get your location. Please enable location services and try again.", "OK");
                return;
            }

            // Shared with the automated LAR-86/87 protocols - one writer for emergency_alerts.
            // A manual press is the "Standard" trigger type.
            string? alertId = await _emergencyAlertService.SendAlertAsync(
                EmergencyAlert.Standard, location.Latitude, location.Longitude);

            if (alertId == null)
            {
                await Shell.Current.DisplayAlert("SOS Failed", "Could not send your SOS alert. Please try again.", "OK");
                return;
            }

            await LARGA.MobileApp.Services.AuditLogWriter.WriteAsync("SosTriggered",
                $"Triggered SOS for {TaxiUnit} ({driverName}) during shift {shiftId}.");

            IsSosAlertVisible = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SOS send failed: {ex.Message}");
            await Shell.Current.DisplayAlert("SOS Failed", "Could not send your SOS alert. Please try again.", "OK");
        }
        finally
        {
            _isSendingSos = false;
        }
    }

    // This method fires every single time the user routes to the Active Shift screen
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        try
        {
            var savedStartTimeStr = Preferences.Get("ShiftStartTime", string.Empty);

            if (string.IsNullOrWhiteSpace(savedStartTimeStr) || !DateTime.TryParse(savedStartTimeStr, out var parsedStartTime))
            {
                _shiftStartTime = ShiftClock.LocalNow;
                Preferences.Set("ShiftStartTime", _shiftStartTime.ToString("o"));

                // Wipe stale timing state for a fresh shift
                _totalBreakTime = TimeSpan.Zero;
                IsPaused = false;
            }
            else
            {
                _shiftStartTime = parsedStartTime;
            }

            ShiftStartTimeDisplay = _shiftStartTime.ToString("hh:mm tt");
            ShiftEndsAt = ReturnDeadlineDisplay();

            // Force the timer to restart if it was stopped during a previous clock-out
            if (!_shiftTimer.IsRunning)
            {
                _shiftTimer.Start();
            }

            _ = InitializeDynamicTaxiAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ApplyQueryAttributes Error: {ex.Message}");

            _shiftStartTime = ShiftClock.LocalNow;
            Preferences.Set("ShiftStartTime", _shiftStartTime.ToString("o"));
            ShiftStartTimeDisplay = _shiftStartTime.ToString("hh:mm tt");
            ShiftEndsAt = ReturnDeadlineDisplay();

            if (!_shiftTimer.IsRunning)
            {
                _shiftTimer.Start();
            }
        }
    }

    // Mirrors the on-screen pause into shifts/{id}.isOnBreak so ManagerWeb's roster, shift
    // logs and dashboard show the driver as On Break rather than Active.
    private async Task SyncBreakStatusAsync(bool isOnBreak)
    {
        try
        {
            string? shiftId = await SecureStorage.GetAsync("ActiveShiftDocumentId");
            if (!string.IsNullOrWhiteSpace(shiftId))
            {
                await _shiftService.SetOnBreakAsync(shiftId, isOnBreak);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Break Sync Error: {ex.Message}");
        }
    }

    private async Task InitializeDynamicTaxiAsync()
    {
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user != null)
        {
            try
            {
                // FIX: Use the specific proxy defined below
                var userProfileDoc = await CrossFirebaseFirestore.Current
                    .GetCollection("users")
                    .GetDocument(user.Uid)
                    .GetDocumentSnapshotAsync<ShiftUserProfileProxy>();

                // Show the unit actually being driven today (a substitute, if one was assigned).
                var dynamicTaxiId = await _shiftService.GetTodaysTaxiIdAsync(userProfileDoc?.Data?.AssignedTaxiId);

                if (!string.IsNullOrWhiteSpace(dynamicTaxiId))
                {
                    var taxi = await _shiftService.GetTaxiUnitAsync(dynamicTaxiId);
                    if (taxi != null)
                    {
                        TaxiUnit = string.IsNullOrWhiteSpace(taxi.PlateNumber)
                            ? taxi.Model
                            : taxi.PlateNumber.Replace("-", " · ");
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }
    }

    public class ShiftUserProfileProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("assignedTaxiId")]
        public string AssignedTaxiId { get; set; }
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        _shiftDuration = (ShiftClock.LocalNow - _shiftStartTime) - _totalBreakTime;
        if (_shiftDuration.TotalSeconds < 0) _shiftDuration = TimeSpan.Zero;

        DurationDisplay = _shiftDuration.ToString(@"hh\:mm\:ss");

        // Counts down to the unit's return time (10:00 PM, ShiftRules) rather than a fixed
        // shift length - the unit is due back at 10 PM however late the driver clocked in.
        DateTime startUtc = _shiftStartTime.ToUniversalTime();
        DateTime nowUtc = ShiftClock.UtcNow;
        TimeSpan untilDeadline = ShiftRules.ReturnDeadlineUtc(startUtc) - nowUtc;

        if (untilDeadline > TimeSpan.Zero)
        {
            _timeRemaining = untilDeadline;
            TimeRemainingDisplay = $"{(int)_timeRemaining.TotalHours:D2}h {_timeRemaining.Minutes:D2}m";
        }
        else
        {
            TimeSpan late = -untilDeadline;
            decimal feeIfReturnedNow = ShiftRules.LateReturnFee(startUtc, nowUtc);
            TimeRemainingDisplay = feeIfReturnedNow > 0
                ? $"LATE {(int)late.TotalHours}h {late.Minutes:D2}m · ₱{feeIfReturnedNow:N0}"
                : $"LATE {late.Minutes}m · no fee until 10:30 PM";
        }
    }

    // Unit return time, shown in the phone's local time (the fleet runs on PH time, so for
    // drivers this reads "10:00 PM").
    private string ReturnDeadlineDisplay() =>
        ShiftRules.ReturnDeadlineUtc(_shiftStartTime.ToUniversalTime()).ToLocalTime().ToString("hh:mm tt");

    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
