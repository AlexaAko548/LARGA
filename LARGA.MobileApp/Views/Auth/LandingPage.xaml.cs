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
            string? role;
            try
            {
                role = await _authService.GetUserRoleAsync(currentUser.Uid);
            }
            catch (Exception ex)
            {
                // Offline/permission errors: stay on Landing so the user can log in manually.
                System.Diagnostics.Debug.WriteLine($"Auto-login role lookup failed: {ex}");
                return;
            }

            // 3. Auto-route them, bypassing the login screens completely
            try
            {
                // Driver, Manager or Assistant Manager (manager dashboard, Manager-only parts left out).
                string? route = LARGA.SharedCore.StaffRoles.HomeRoute(role);
                if (route != null)
                {
                    LARGA.MobileApp.Services.StaffSession.Role = role!.Trim();
                    bool mustChangePassword;
                    try
                    {
                        mustChangePassword = await _authService.MustChangePasswordAsync(currentUser.Uid);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"mustChangePassword lookup failed: {ex.Message}");
                        mustChangePassword = false;
                    }

                    await Shell.Current.GoToAsync(route);
                    await ForcedPasswordChangePage.ShowIfRequiredAsync(mustChangePassword);
                }
            }
            catch (Exception ex)
            {
                // Stay on Landing; the user can still log in manually.
                System.Diagnostics.Debug.WriteLine($"Auto-login routing failed: {ex}");
            }
        }
    }
}