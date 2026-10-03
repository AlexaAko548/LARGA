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
using LARGA.SharedCore.Services;
using LARGA.Shared.Models.Entities;

namespace LARGA.MobileApp.ViewModels.Driver;

public class ActiveShiftViewModel : INotifyPropertyChanged, IQueryAttributable
{
    private readonly IShiftManagementService _shiftService;
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

    public ActiveShiftViewModel(IShiftManagementService shiftService)
    {
        _shiftService = shiftService;

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
            _pauseStartTime = DateTime.Now;
            _shiftTimer.Stop();
        });

        ResumeShiftCommand = new Command(() =>
        {
            IsPaused = false;
            _totalBreakTime += (DateTime.Now - _pauseStartTime);
            _shiftTimer.Start();
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

            var user = CrossFirebaseAuth.Current.CurrentUser;
            string driverId = user?.Uid ?? string.Empty;
            string driverName = string.IsNullOrWhiteSpace(user?.DisplayName) ? "Unknown Driver" : user.DisplayName;

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

            var alert = new EmergencySosProxy
            {
                ShiftId = shiftId,
                DriverId = driverId,
                DriverName = driverName,
                TaxiUnit = TaxiUnit,
                Latitude = location.Latitude,
                Longitude = location.Longitude,
                IsResolved = false,
                Timestamp = DateTime.UtcNow,
            };

            await CrossFirebaseFirestore.Current
                .GetCollection("emergency_alerts")
                .AddDocumentAsync(alert);

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

    private class EmergencySosProxy
    {
        [Plugin.Firebase.Firestore.FirestoreProperty("shiftId")]
        public string ShiftId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("driverId")]
        public string DriverId { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("driverName")]
        public string DriverName { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("taxiUnit")]
        public string TaxiUnit { get; set; } = string.Empty;

        [Plugin.Firebase.Firestore.FirestoreProperty("latitude")]
        public double Latitude { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("longitude")]
        public double Longitude { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("isResolved")]
        public bool IsResolved { get; set; }

        [Plugin.Firebase.Firestore.FirestoreProperty("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    // This method fires every single time the user routes to the Active Shift screen
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        try
        {
            var savedStartTimeStr = Preferences.Get("ShiftStartTime", string.Empty);

            if (string.IsNullOrWhiteSpace(savedStartTimeStr) || !DateTime.TryParse(savedStartTimeStr, out var parsedStartTime))
            {
                _shiftStartTime = DateTime.Now;
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
            ShiftEndsAt = _shiftStartTime.AddHours(10).ToString("hh:mm tt");

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

            _shiftStartTime = DateTime.Now;
            Preferences.Set("ShiftStartTime", _shiftStartTime.ToString("o"));
            ShiftStartTimeDisplay = _shiftStartTime.ToString("hh:mm tt");
            ShiftEndsAt = _shiftStartTime.AddHours(10).ToString("hh:mm tt");

            if (!_shiftTimer.IsRunning)
            {
                _shiftTimer.Start();
            }
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

                var dynamicTaxiId = userProfileDoc?.Data?.AssignedTaxiId;

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
        _shiftDuration = (DateTime.Now - _shiftStartTime) - _totalBreakTime;
        if (_shiftDuration.TotalSeconds < 0) _shiftDuration = TimeSpan.Zero;

        DurationDisplay = _shiftDuration.ToString(@"hh\:mm\:ss");

        var newRemaining = TimeSpan.FromHours(10) - _shiftDuration;
        if (newRemaining.TotalSeconds > 0)
        {
            _timeRemaining = newRemaining;
            TimeRemainingDisplay = $"{_timeRemaining.Hours:D2}h {_timeRemaining.Minutes:D2}m";
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
