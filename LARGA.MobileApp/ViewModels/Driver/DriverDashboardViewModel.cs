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
    public ICommand ClockOutCommand { get; }
    public ICommand OpenMessagesCommand { get; }

    public DriverDashboardViewModel()
    {
        ClockInCommand = new Command(async () => await Shell.Current.GoToAsync("pre-shift-step1"));
        ClockOutCommand = new Command(async () => await Shell.Current.GoToAsync("end-shift-step1"));
        OpenMessagesCommand = new Command(async () => await Shell.Current.GoToAsync("message-manager"));
    }

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}