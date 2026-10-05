using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using LARGA.SharedCore;
using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.ViewModels.Driver;

/// <summary>
/// "Wait for the manager's evaluation" (paper Ch. IV, driver clock-in process): the pre-shift
/// inspection flagged something, so PreShiftStep2 sent a clock-in request instead of clocking
/// in. This screen checks the request every few seconds:
/// - Approved: clocks in exactly as an unflagged inspection would (same ShiftRules checks),
///   files the pre-shift checklist, and goes to Active Shift;
/// - Denied: shows the manager's note - the unit was escalated to maintenance;
/// - the driver can also withdraw the request.
/// The request ID is kept in Preferences so the wait survives closing the app.
/// </summary>
public class ClockInPendingViewModel : BindableObject
{
    public const string PendingRequestKey = "PendingClockInRequestId";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IShiftManagementService _shiftService;
    private readonly IGpsTelemetryService _telemetryService;
    private IDispatcherTimer? _timer;
    private bool _isChecking;
    private bool _isFinished;

    public ClockInPendingViewModel(IShiftManagementService shiftService, IGpsTelemetryService telemetryService)
    {
        _shiftService = shiftService;
        _telemetryService = telemetryService;
        CancelRequestCommand = new Command(async () => await CancelRequestAsync());
        BackToDashboardCommand = new Command(async () => await BackToDashboardAsync());
    }

    public ICommand CancelRequestCommand { get; }
    public ICommand BackToDashboardCommand { get; }

    private string _title = "Waiting for the manager";
    public string Title { get => _title; private set { _title = value; OnPropertyChanged(); } }

    private string _message = "Your pre-shift inspection flagged an issue, so your clock-in was sent to the manager. Stay near the unit - your shift starts as soon as it's approved.";
    public string Message { get => _message; private set { _message = value; OnPropertyChanged(); } }

    private string _reasons = string.Empty;
    public string Reasons { get => _reasons; private set { _reasons = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasReasons)); } }
    public bool HasReasons => !string.IsNullOrWhiteSpace(Reasons);

    private string _managerNote = string.Empty;
    public string ManagerNote { get => _managerNote; private set { _managerNote = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasManagerNote)); } }
    public bool HasManagerNote => !string.IsNullOrWhiteSpace(ManagerNote);

    private bool _isWaiting = true;
    public bool IsWaiting { get => _isWaiting; private set { _isWaiting = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsDone)); } }
    public bool IsDone => !IsWaiting;

    public void Start(IDispatcher dispatcher)
    {
        _isFinished = false;
        _timer ??= dispatcher.CreateTimer();
        _timer.Interval = PollInterval;
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
        _ = CheckAsync();
    }

    public void Stop() => _timer?.Stop();

    private void OnTick(object? sender, EventArgs e) => _ = CheckAsync();

    private async Task CheckAsync()
    {
        if (_isChecking || _isFinished)
        {
            return;
        }

        _isChecking = true;
        try
        {
            string requestId = Preferences.Get(PendingRequestKey, string.Empty);
            if (string.IsNullOrEmpty(requestId))
            {
                Finish("No pending request", "There's no clock-in waiting for approval.");
                return;
            }

            ClockInRequestStatus? request = await _shiftService.GetClockInRequestAsync(requestId);
            if (request is null)
            {
                return; // offline / not readable yet - try again next tick
            }

            Reasons = request.FlagReasons.Replace("; ", "\n• ") is { Length: > 0 } r ? "• " + r : string.Empty;
            ManagerNote = request.ManagerNote ?? string.Empty;

            switch (request.Status)
            {
                case "Approved":
                    await StartApprovedShiftAsync(requestId, request);
                    break;
                case "ClockedIn":
                    // Already started (e.g. from another screen) - nothing left to wait for.
                    Preferences.Remove(PendingRequestKey);
                    Finish("Shift started", "The manager approved your clock-in and your shift has started.");
                    break;
                case "Denied":
                    Preferences.Remove(PendingRequestKey);
                    Finish("Clock-in denied",
                        "The manager decided the unit can't go out until the issue is fixed. It has been sent to maintenance.");
                    break;
                case "Cancelled":
                    Preferences.Remove(PendingRequestKey);
                    Finish("Request withdrawn", "You withdrew this clock-in request.");
                    break;
            }
        }
        finally
        {
            _isChecking = false;
        }
    }

    /// <summary>Approved: the same clock-in as an unflagged inspection (PreShiftStep2).</summary>
    private async Task StartApprovedShiftAsync(string requestId, ClockInRequestStatus request)
    {
        string newDocumentId;
        try
        {
            newDocumentId = await _shiftService.ClockInAsync(request.TaxiId, request.StartMileage);
        }
        catch (InvalidOperationException rule)
        {
            // Operating-day rule (before 6:00 AM, or a shift already open).
            Stop();
            _isFinished = true;
            Title = "Can't start shift";
            Message = rule.Message;
            IsWaiting = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(newDocumentId))
        {
            return; // try again next tick
        }

        _isFinished = true;
        Stop();

        await SecureStorage.SetAsync("ActiveShiftDocumentId", newDocumentId);
        Preferences.Set("CurrentShiftId", newDocumentId);
        Preferences.Set("IsShiftActive", true);
        Preferences.Set("ShiftStartTime", ShiftClock.LocalNow.ToString("o"));
        Preferences.Remove(PendingRequestKey);

        // LAR-77 Contextual Auto-Cutoff Protocol: an approved clock-in is still a successful
        // clock-in, so telemetry starts here too (PreShiftStep2 covers the unflagged path).
        _telemetryService.Start(newDocumentId);

        // The inspection the manager approved becomes this shift's pre-shift checklist (its
        // photos were uploaded with the request). Never blocks the shift.
        try
        {
            request.Checklist.ShiftId = newDocumentId;
            await _shiftService.SubmitHandoverChecklistAsync(request.Checklist);
            await _shiftService.MarkClockInRequestUsedAsync(requestId, newDocumentId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Approved Clock-in Follow-up Error: {ex.Message}");
        }

        await Shell.Current.DisplayAlert("Shift approved!", "The manager approved your clock-in. Drive safe.", "OK");
        await Shell.Current.GoToAsync("active-shift");
    }

    private async Task CancelRequestAsync()
    {
        string requestId = Preferences.Get(PendingRequestKey, string.Empty);
        bool confirm = await Shell.Current.DisplayAlert("Withdraw request?",
            "Your clock-in request will be withdrawn. You can redo the pre-shift inspection afterwards.", "Withdraw", "Keep waiting");
        if (!confirm)
        {
            return;
        }

        if (!string.IsNullOrEmpty(requestId))
        {
            try
            {
                await _shiftService.CancelClockInRequestAsync(requestId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Cancel Clock-in Request Error: {ex.Message}");
            }
        }

        Preferences.Remove(PendingRequestKey);
        Stop();
        await Shell.Current.GoToAsync("//driver-dashboard");
    }

    private async Task BackToDashboardAsync()
    {
        Stop();
        await Shell.Current.GoToAsync("//driver-dashboard");
    }

    private void Finish(string title, string message)
    {
        _isFinished = true;
        Stop();
        Title = title;
        Message = message;
        IsWaiting = false;
    }
}
