using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Driver;

public class PreShiftStep1ViewModel : BindableObject
{
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

    public ICommand PassItemCommand { get; }
    public ICommand ReportDefectCommand { get; }
    public ICommand NextCommand { get; }

    public PreShiftStep1ViewModel()
    {
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

        NextCommand = new Command(async () => await Shell.Current.GoToAsync("pre-shift-step2"));
    }

    private void UpdateProgress(string item)
    {
        // Update the top progress bar and text
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressRatio));

        // Dynamically hide/show the buttons for the specific row clicked
        OnPropertyChanged($"{item}CheckVisible");
        OnPropertyChanged($"{item}CloseVisible");
    }
}