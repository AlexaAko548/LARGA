using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using LARGA.SharedCore.Services;
using System.Threading.Tasks;

namespace LARGA.MobileApp.ViewModels.Driver;

public class PreShiftStep1ViewModel : BindableObject, IQueryAttributable
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
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressRatio));
        OnPropertyChanged($"{item}CheckVisible");
        OnPropertyChanged($"{item}CloseVisible");
        OnPropertyChanged(nameof(IsComplete));
    }

    public async void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("defectSubmitted", out var value) && value?.ToString() == "true")
        {
            await Shell.Current.DisplayAlert("On pause.", "Wait for the Manager's evaluation.", "OK");
            await Shell.Current.DisplayAlert("Shift approved!", string.Empty, "OK");
        }
    }
}