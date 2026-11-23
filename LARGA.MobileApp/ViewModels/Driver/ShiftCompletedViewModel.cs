using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.MobileApp.Services;
using LARGA.SharedCore.Ledger;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using Plugin.Firebase.Auth;

namespace LARGA.MobileApp.ViewModels.Driver;

public class ShiftCompletedViewModel : BindableObject, IQueryAttributable
{
    /// <summary>Late-return fee and low-fuel penalty from clock-out (ShiftRules), passed by
    /// End Shift step 2.</summary>
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        decimal lateFee = query.TryGetValue("lateFee", out var late) && late is decimal l ? l : 0m;
        decimal fuelPenalty = query.TryGetValue("fuelPenalty", out var fuel) && fuel is decimal f ? f : 0m;

        // Was a hardcoded 800 - a manager-configured system_configs/global.defaultBoundaryRate (e.g. 900)
        // never showed up here even though every other screen already reads it (QuickLedgerCalculator).
        BoundaryAmountDisplay = $"₱ {QuickLedgerCalculator.FallbackBoundaryRate + lateFee + fuelPenalty:N0}";
        _ = UpdateBoundaryDisplayAsync(lateFee, fuelPenalty);

        var notes = new List<string>();
        if (lateFee > 0)
        {
            notes.Add($"₱{lateFee:N0} late-return fee (unit due back by 10:00 PM)");
        }
        if (fuelPenalty > 0)
        {
            notes.Add($"₱{fuelPenalty:N0} penalty for returning below half-tank");
        }
        LateFeeNote = notes.Count == 0 ? string.Empty : "Includes " + string.Join(" and ", notes) + ".";
    }

    private string _lateFeeNote = string.Empty;
    public string LateFeeNote
    {
        get => _lateFeeNote;
        set { _lateFeeNote = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLateFee)); }
    }

    public bool HasLateFee => !string.IsNullOrEmpty(LateFeeNote);

    private string _currentDateDisplay = string.Empty;
    public string CurrentDateDisplay
    {
        get => _currentDateDisplay;
        set { _currentDateDisplay = value; OnPropertyChanged(); }
    }

    private string _completionMessage = string.Empty;
    public string CompletionMessage
    {
        get => _completionMessage;
        set { _completionMessage = value; OnPropertyChanged(); }
    }

    private string _boundaryAmountDisplay = $"₱ {QuickLedgerCalculator.FallbackBoundaryRate:N0}";
    public string BoundaryAmountDisplay
    {
        get => _boundaryAmountDisplay;
        set { _boundaryAmountDisplay = value; OnPropertyChanged(); }
    }

    private async Task UpdateBoundaryDisplayAsync(decimal lateFee, decimal fuelPenalty)
    {
        decimal boundaryRate = await QuickLedgerService.ReadDefaultBoundaryRateAsync();
        BoundaryAmountDisplay = $"₱ {boundaryRate + lateFee + fuelPenalty:N0}";
    }

    public ICommand ReturnToHomeCommand { get; }

    public ShiftCompletedViewModel()
    {
        // Dynamically set today's date
        CurrentDateDisplay = DateTime.Now.ToString("dddd, dd MMM yyyy");
        CompletionMessage = "Great work!\nYour shift has been successfully logged and GPS tracking is ";

        ReturnToHomeCommand = new Command(async () =>
        {
            Preferences.Remove("IsShiftActive");
            Preferences.Remove("ShiftStartTime");
            await Shell.Current.GoToAsync("//driver-dashboard");
        });

        InitializeDynamicData();
    }

    private void InitializeDynamicData()
    {
        var user = CrossFirebaseAuth.Current.CurrentUser;
        if (user != null)
        {
            // Extract just the first name
            var firstName = string.IsNullOrWhiteSpace(user.DisplayName) ? "Driver" : user.DisplayName.Split(' ')[0];
            CompletionMessage = $"Great work, {firstName}!\nYour shift has been successfully logged and GPS tracking is ";
        }
    }
}