using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Extensions.DependencyInjection;

namespace LARGA.MobileApp.Views.Driver;

public partial class ReportsPage : ContentPage
{
    private readonly ReportsViewModel _viewModel;

    public ReportsPage()
    {
        InitializeComponent();
        _viewModel = Application.Current?.Handler?.MauiContext?.Services.GetService<ReportsViewModel>() ?? new ReportsViewModel();
        BindingContext = _viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_viewModel.LoadReportsCommand.CanExecute(null))
        {
            _viewModel.LoadReportsCommand.Execute(null);
        }
    }
}