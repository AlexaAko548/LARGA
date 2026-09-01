using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Driver;

public class DriverDashboardViewModel : INotifyPropertyChanged
{
    private bool _isOffline = true;
    public event PropertyChangedEventHandler? PropertyChanged;

    // Toggles the UI between Offline (True) and Online (False)
    public bool IsOffline
    {
        get => _isOffline;
        set
        {
            if (_isOffline == value) return;
            _isOffline = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOnline));
        }
    }

    public bool IsOnline => !IsOffline;

    public ICommand ClockInCommand { get; }
    public ICommand OpenMessagesCommand { get; }

    public DriverDashboardViewModel()
    {
        // Temporarily toggles the UI state to Online
        ClockInCommand = new Command(async () => await Shell.Current.GoToAsync("pre-shift-step1"));

        // Navigates to the message screen
        OpenMessagesCommand = new Command(async () => await Shell.Current.GoToAsync("message-manager"));
    }

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}