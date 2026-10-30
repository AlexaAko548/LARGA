using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.ViewModels.Driver;

public class EndShiftStep1ViewModel : BindableObject
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

    // Review follow-up: these pages hard-coded another unit's plate instead of showing the one
    // actually on this shift - every driver saw the same wrong plate. Same AssignedUnitPlate
    // pattern (and the same GetCurrentUserAssignedTaxiAsync source) PreShiftStep1ViewModel uses.
    private string _assignedUnitPlate = "Loading...";
    public string AssignedUnitPlate
    {
        get => _assignedUnitPlate;
        private set { _assignedUnitPlate = value; OnPropertyChanged(); }
    }

    public string ProgressText => $"{_items.Values.Count(v => v != null)} / 5 completed";
    public double ProgressRatio => _items.Values.Count(v => v != null) / 5.0;

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

    // Unlocks the Next button
    public bool IsComplete => _items.Values.All(v => v != null);

    public ICommand PassItemCommand { get; }
    public ICommand ReportDefectCommand { get; }
    public ICommand NextCommand { get; }

    public EndShiftStep1ViewModel(IShiftManagementService shiftService)
    {
        _shiftService = shiftService;
        _ = LoadAssignedUnitAsync();

        PassItemCommand = new Command<string>((item) =>
        {
            _items[item] = _items[item] == true ? null : true;
            UpdateProgress(item);
        });

        ReportDefectCommand = new Command<string>(async (item) =>
        {
            if (_items[item] == false)
            {
                _items[item] = null;
                UpdateProgress(item);
            }
            else
            {
                _items[item] = false;
                UpdateProgress(item);
                await LARGA.MobileApp.Services.NavigationGate.GoToAsync($"vehicle-defect-page?item={item}");
            }
        });

        NextCommand = new Command(async () =>
        {
            if (!IsComplete)
            {
                await Shell.Current.DisplayAlert("Incomplete", "Please complete all 5 inspection items before proceeding.", "OK");
                return;
            }
            await LARGA.MobileApp.Services.NavigationGate.GoToAsync("end-shift-step2", new Dictionary<string, object>
            {
                { "inspection", _items.ToDictionary(kv => kv.Key, kv => kv.Value == true) },
            });
        });
    }

    private async Task LoadAssignedUnitAsync()
    {
        using var _loading = LARGA.MobileApp.Services.LoadingState.Begin(); // LAR-117: app-wide loading indicator
        try
        {
            var taxi = await _shiftService.GetCurrentUserAssignedTaxiAsync();
            if (taxi != null)
            {
                AssignedUnitPlate = string.IsNullOrWhiteSpace(taxi.PlateNumber)
                    ? taxi.Model
                    : taxi.PlateNumber.Replace("-", " · ");
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Assigned Unit Error: {ex.Message}");
        }
    }

    private void UpdateProgress(string item)
    {
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressRatio));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged($"{item}CheckVisible");
        OnPropertyChanged($"{item}CloseVisible");
    }
}