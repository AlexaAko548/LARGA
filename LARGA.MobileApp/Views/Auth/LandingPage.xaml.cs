using LARGA.MobileApp.ViewModels.Auth;
using Microsoft.Maui.Controls;
using Plugin.Firebase.Auth;
using LARGA.SharedCore.Services;
using System;

namespace LARGA.MobileApp.Views.Auth;

public partial class LandingPage : ContentPage
{
    private readonly IFirebaseAuthService _authService;

    // Inject the IFirebaseAuthService here alongside the ViewModel
    public LandingPage(LandingViewModel viewModel, IFirebaseAuthService authService)
    {
        InitializeComponent();
        BindingContext = viewModel;
        _authService = authService;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // 1. Check if Firebase already has a saved session on the device
        var currentUser = CrossFirebaseAuth.Current.CurrentUser;

        if (currentUser != null)
        {
            // 2. Fetch their role to know which dashboard to load
            var role = await _authService.GetUserRoleAsync(currentUser.Uid);

            // 3. Auto-route them, bypassing the login screens completely
            if (role?.Equals("Driver", StringComparison.OrdinalIgnoreCase) == true)
            {
                await Shell.Current.GoToAsync("//driver-dashboard");
            }
            else if (role?.Equals("Manager", StringComparison.OrdinalIgnoreCase) == true)
            {
                await Shell.Current.GoToAsync("//manager-dashboard");
            }
        }
    }
}