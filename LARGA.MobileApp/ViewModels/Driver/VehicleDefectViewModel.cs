using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using System.Threading.Tasks;

namespace LARGA.MobileApp.ViewModels.Driver;

[QueryProperty(nameof(ChecklistItem), "item")]
public class VehicleDefectViewModel : BindableObject
{
    private string _checklistItem = string.Empty;
    private string _titleReport = string.Empty;
    private string _description = string.Empty;
    private string _priority = "High";
    private string? _photoPath;
    private ImageSource? _defectPhoto;

    public string ChecklistItem
    {
        get => _checklistItem;
        set { _checklistItem = value; OnPropertyChanged(); }
    }

    public string TitleReport
    {
        get => _titleReport;
        set { _titleReport = value; OnPropertyChanged(); }
    }

    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged(); }
    }

    public ImageSource? DefectPhoto
    {
        get => _defectPhoto;
        set
        {
            _defectPhoto = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPhoto));
        }
    }

    public bool HasPhoto => DefectPhoto != null;

    public bool IsLow
    {
        get => _priority == "Low";
        set { if (value) { _priority = "Low"; RefreshPriority(); } }
    }

    public bool IsMedium
    {
        get => _priority == "Medium";
        set { if (value) { _priority = "Medium"; RefreshPriority(); } }
    }

    public bool IsHigh
    {
        get => _priority == "High";
        set { if (value) { _priority = "High"; RefreshPriority(); } }
    }

    private void RefreshPriority()
    {
        OnPropertyChanged(nameof(IsLow));
        OnPropertyChanged(nameof(IsMedium));
        OnPropertyChanged(nameof(IsHigh));
    }

    public ICommand AttachPhotoCommand { get; }
    public ICommand SubmitReportCommand { get; }

    public VehicleDefectViewModel()
    {
        AttachPhotoCommand = new Command(async () => await AttachPhotoAsync());

        SubmitReportCommand = new Command(async () =>
        {
            if (string.IsNullOrWhiteSpace(TitleReport) || string.IsNullOrWhiteSpace(Description))
            {
                await Shell.Current.DisplayAlert("Incomplete Report", "Please provide both a title and a description before submitting.", "OK");
                return;
            }

            // TODO: save MaintenanceRecord to Firestore with TitleReport, Description, PriorityLevel, _photoPath
            await Shell.Current.GoToAsync($"..?defectSubmitted=true");
        });
    }

    private async Task AttachPhotoAsync()
    {
        try
        {
            if (MediaPicker.Default.IsCaptureSupported)
            {
                var photo = await MediaPicker.Default.CapturePhotoAsync();
                if (photo != null)
                {
                    _photoPath = photo.FullPath;
                    var stream = await photo.OpenReadAsync();
                    DefectPhoto = ImageSource.FromStream(() => stream);
                }
            }
        }
        catch (System.Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Camera failed: {ex.Message}", "OK");
        }
    }
}