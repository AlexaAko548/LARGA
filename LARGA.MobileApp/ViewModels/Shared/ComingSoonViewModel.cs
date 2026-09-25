using System.Windows.Input;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Shared;

/// <summary>
/// Generic "not built yet" placeholder for stubbed nav targets (e.g. Fleet Registry,
/// Change Password) - takes its title from the query string so one page/viewmodel covers
/// every stub instead of a near-duplicate page per feature.
/// </summary>
[QueryProperty(nameof(Title), "title")]
public class ComingSoonViewModel : BindableObject
{
    private string _title = "Coming Soon";
    public string Title
    {
        get => _title;
        set { _title = string.IsNullOrWhiteSpace(value) ? "Coming Soon" : value; OnPropertyChanged(); }
    }

    public ICommand GoBackCommand { get; }

    public ComingSoonViewModel()
    {
        GoBackCommand = new Command(async () => await Shell.Current.GoToAsync(".."));
    }
}
