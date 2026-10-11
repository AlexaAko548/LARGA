using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
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

        BoundaryAmountDisplay = $"₱ {800 + lateFee + fuelPenalty:N0}";

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

    private string _boundaryAmountDisplay = "₱ 800";
    public string BoundaryAmountDisplay
    {
        get => _boundaryAmountDisplay;
        set { _boundaryAmountDisplay = value; OnPropertyChanged(); }
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