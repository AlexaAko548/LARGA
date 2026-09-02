using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.ViewModels.Driver;

public class ActiveShiftViewModel : INotifyPropertyChanged
{
    private readonly IShiftManagementService _shiftService;
    private readonly IDispatcherTimer _shiftTimer;
    private TimeSpan _shiftDuration;
    private TimeSpan _timeRemaining;

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

    public string StatusBannerText => IsPaused ? "On Break · GPS Tracking On" : "Active Shift · GPS Tracking On";
    public Color StatusBannerColor => IsPaused ? Colors.Yellow : Colors.Lime;
    public string ShiftStatus => IsPaused ? "Shift Paused." : "On the Road.";

    // Data-bound dynamic properties
    private string _taxiUnit = "Loading...";
    public string TaxiUnit
    {
        get => _taxiUnit;
        set { _taxiUnit = value; OnPropertyChanged(); }
    }

    private string _shiftStartTimeDisplay;
    public string ShiftStartTimeDisplay
    {
        get => _shiftStartTimeDisplay;
        set { _shiftStartTimeDisplay = value; OnPropertyChanged(); }
    }

    private string _shiftEndsAt;
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

    private string _timeRemainingDisplay;
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

    public ActiveShiftViewModel(IShiftManagementService shiftService)
    {
        _shiftService = shiftService;

        // Initialize Dynamic Shift Times (Assuming standard 10-hour shift)
        var now = DateTime.Now;
        ShiftStartTimeDisplay = now.ToString("hh:mm tt");
        ShiftEndsAt = now.AddHours(10).ToString("hh:mm tt");

        _shiftDuration = TimeSpan.Zero;
        _timeRemaining = TimeSpan.FromHours(10);
        TimeRemainingDisplay = $"{_timeRemaining.Hours:D2}h {_timeRemaining.Minutes:D2}m";

        _shiftTimer = Application.Current.Dispatcher.CreateTimer();
        _shiftTimer.Interval = TimeSpan.FromSeconds(1);
        _shiftTimer.Tick += OnTimerTick;
        _shiftTimer.Start();

        PauseShiftCommand = new Command(() => { IsPaused = true; _shiftTimer.Stop(); });
        ResumeShiftCommand = new Command(() => { IsPaused = false; _shiftTimer.Start(); });

        ClockOutCommand = new Command(() => IsClockOutAlertVisible = true);
        CancelClockOutCommand = new Command(() => IsClockOutAlertVisible = false);
        ConfirmClockOutCommand = new Command(async () =>
        {
            IsClockOutAlertVisible = false;
            _shiftTimer.Stop();
            await Shell.Current.GoToAsync("end-of-shift");
        });

        SendSosCommand = new Command(() => IsSosAlertVisible = true);
        DismissSosCommand = new Command(() => IsSosAlertVisible = false);

        // Fetch assigned taxi dynamically from Firestore
        _ = LoadAssignedTaxiAsync("TX-01"); // Pass the dynamically assigned TaxiId here later
    }

    private async Task LoadAssignedTaxiAsync(string taxiId)
    {
        var taxi = await _shiftService.GetTaxiUnitAsync(taxiId);
        if (taxi != null)
        {
            // Format to match the UI: GHK · 4471 · MP
            TaxiUnit = string.IsNullOrWhiteSpace(taxi.PlateNumber)
                ? taxi.Model
                : taxi.PlateNumber.Replace("-", " · ");
        }
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        _shiftDuration = _shiftDuration.Add(TimeSpan.FromSeconds(1));
        DurationDisplay = _shiftDuration.ToString(@"hh\:mm\:ss");

        if (_timeRemaining.TotalSeconds > 0)
        {
            _timeRemaining = _timeRemaining.Subtract(TimeSpan.FromSeconds(1));
            TimeRemainingDisplay = $"{_timeRemaining.Hours:D2}h {_timeRemaining.Minutes:D2}m";
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}