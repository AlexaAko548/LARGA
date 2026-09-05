using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using LARGA.SharedCore.Services;
using LARGA.Shared.Models.Entities;

namespace LARGA.MobileApp.ViewModels.Driver;

public class ActiveShiftViewModel : INotifyPropertyChanged
{
    private readonly IShiftManagementService _shiftService;
    private readonly IDispatcherTimer _shiftTimer;
    private TimeSpan _shiftDuration;
    private TimeSpan _timeRemaining;
    private DateTime _shiftStartTime;

    // Fixed: Added the missing pause variables
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

    public string StatusBannerText => IsPaused ? "On Break · GPS Tracking On" : "Active Shift · GPS Tracking On";
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

    public string Distance { get; set; } = "0km";
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

        var savedStartTimeStr = Preferences.Get("ShiftStartTime", string.Empty);
        if (string.IsNullOrEmpty(savedStartTimeStr))
        {
            _shiftStartTime = DateTime.Now;
            Preferences.Set("ShiftStartTime", _shiftStartTime.ToString("o"));
        }
        else
        {
            _shiftStartTime = DateTime.Parse(savedStartTimeStr);
        }

        ShiftStartTimeDisplay = _shiftStartTime.ToString("hh:mm tt");
        ShiftEndsAt = _shiftStartTime.AddHours(10).ToString("hh:mm tt");

        _shiftDuration = DateTime.Now - _shiftStartTime;
        _timeRemaining = TimeSpan.FromHours(10) - _shiftDuration;

        if (_timeRemaining.TotalSeconds < 0) _timeRemaining = TimeSpan.Zero;
        TimeRemainingDisplay = $"{_timeRemaining.Hours:D2}h {_timeRemaining.Minutes:D2}m";

        _shiftTimer = Application.Current.Dispatcher.CreateTimer();
        _shiftTimer.Interval = TimeSpan.FromSeconds(1);
        _shiftTimer.Tick += OnTimerTick;
        _shiftTimer.Start();

        RequestPauseCommand = new Command(() => IsPauseAlertVisible = true);
        CancelPauseCommand = new Command(() => IsPauseAlertVisible = false);
        ConfirmPauseCommand = new Command(() =>
        {
            IsPauseAlertVisible = false;
            IsPaused = true;
            _pauseStartTime = DateTime.Now;
            _shiftTimer.Stop();
        });

        // Fixed: Calculates and stores the exact duration of the break
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

        SendSosCommand = new Command(() => IsSosAlertVisible = true);
        DismissSosCommand = new Command(() => IsSosAlertVisible = false);

        _ = InitializeDynamicTaxiAsync();
    }

    private async Task InitializeDynamicTaxiAsync()
    {
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user != null)
        {
            try
            {
                var userProfileDoc = await CrossFirebaseFirestore.Current
                    .GetCollection("users")
                    .GetDocument(user.Uid)
                    .GetDocumentSnapshotAsync<UserProfile>();

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

    private void OnTimerTick(object sender, EventArgs e)
    {
        // Fixed: Subtracts the total break time so the timer doesn't jump forward
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