using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Driver;

public class EndShiftStep1ViewModel : BindableObject
{
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

    // Unlocks the Next button
    public bool IsComplete => _items.Values.All(v => v != null);

    public ICommand PassItemCommand { get; }
    public ICommand ReportDefectCommand { get; }
    public ICommand NextCommand { get; }

    public EndShiftStep1ViewModel()
    {
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
            if (!IsComplete)
            {
                await Shell.Current.DisplayAlert("Incomplete", "Please complete all 5 inspection items before proceeding.", "OK");
                return;
            }
            await Shell.Current.GoToAsync("end-shift-step2");
        });
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