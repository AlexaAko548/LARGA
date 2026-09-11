using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using LARGA.SharedCore.Services;
using System.Threading.Tasks;

namespace LARGA.MobileApp.ViewModels.Driver;

public class PreShiftStep1ViewModel : BindableObject
{
    private readonly IShiftManagementService _shiftService;
    private readonly Dictionary<string, bool?> _items = new()
    {
        { "Tires", null },
        { "Hood", null },
        { "Lights", null },
        { "Interior", null },
        { "Exterior", null }
    };

    // Live Progress Trackers
    public string ProgressText => $"{_items.Values.Count(v => v != null)} / 5 completed";
    public double ProgressRatio => _items.Values.Count(v => v != null) / 5.0;

    // Dynamic Visibility Properties (If True, hide the X. If False, hide the Check.)
    public bool TiresCheckVisible => _items["Tires"] != false;
    public bool TiresCloseVisible => _items["Tires"] != true;

    public bool HoodCheckVisible => _items["Hood"] != false;
    public bool HoodCloseVisible => _items["Hood"] != true;

    public bool LightsCheckVisible => _items["Lights"] != false;
    public bool LightsCloseVisible => _items["Lights"] != true;

    public bool InteriorCheckVisible => _items["Interior"] != false;
    public bool InteriorCloseVisible => _items["Interior"] != true;

    public bool ExteriorCheckVisible => _items["Exterior"] != false;
    public bool ExteriorCloseVisible => _items["Exterior"] != true;
    public bool IsComplete => _items.Values.All(v => v != null);

    private string _assignedUnitPlate = "Loading...";
    public string AssignedUnitPlate
    {
        get => _assignedUnitPlate;
        private set { _assignedUnitPlate = value; OnPropertyChanged(); }
    }

    public ICommand PassItemCommand { get; }
    public ICommand ReportDefectCommand { get; }
    public ICommand NextCommand { get; }

    public PreShiftStep1ViewModel(IShiftManagementService shiftService)
    {
        _shiftService = shiftService;
        _ = LoadAssignedUnitAsync();

        PassItemCommand = new Command<string>((item) =>
        {
            // Toggle off if already selected, otherwise set to Passed
            _items[item] = _items[item] == true ? null : true;
            UpdateProgress(item);
        });

        ReportDefectCommand = new Command<string>(async (item) =>
        {
            if (_items[item] == false)
            {
                // Toggle off if already selected
                _items[item] = null;
                UpdateProgress(item);
            }
            else
            {
                // Set to Failed and Route
                _items[item] = false;
                UpdateProgress(item);
                await Shell.Current.GoToAsync($"vehicle-defect-page?item={item}");
            }
        });

        NextCommand = new Command(async () =>
        {
            if (_items.Values.Any(v => v == null))
            {
                await Shell.Current.DisplayAlert("Incomplete", "Please complete all 5 inspection items before proceeding.", "OK");
                return;
            }
            await Shell.Current.GoToAsync("pre-shift-step2");
        });
    }

    private async Task LoadAssignedUnitAsync()
    {
        try
        {
            var taxi = await _shiftService.GetCurrentUserAssignedTaxiAsync();
            if (taxi != null)
            {
                AssignedUnitPlate = string.IsNullOrWhiteSpace(taxi.PlateNumber)
                    ? taxi.Model
                    : taxi.PlateNumber.Replace("-", "·");
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Assigned Unit Error: {ex.Message}");
        }
    }

    private void UpdateProgress(string item)
    {
        // Update the top progress bar and text
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressRatio));

        // Dynamically hide/show the buttons for the specific row clicked
        OnPropertyChanged($"{item}CheckVisible");
        OnPropertyChanged($"{item}CloseVisible");

        OnPropertyChanged(nameof(IsComplete));
    }
}