using System.Threading.Tasks;
using LARGA.MobileApp.ViewModels.Auth;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Views.Auth;

/// <summary>
/// Shown modally over the dashboard while users/{uid}.mustChangePassword is true. Back does nothing;
/// the page closes only after the password is changed (or the user signs out).
/// </summary>
public partial class ForcedPasswordChangePage : ContentPage
{
    private static bool _isShowing;

    public ForcedPasswordChangePage()
    {
        InitializeComponent();
        var viewModel = new ForcedPasswordChangeViewModel();
        viewModel.Completed = CloseAsync;
        BindingContext = viewModel;
    }

    /// <summary>Puts the screen up (once) when <paramref name="mustChangePassword"/> is true.</summary>
    public static async Task ShowIfRequiredAsync(bool mustChangePassword)
    {
        if (!mustChangePassword || _isShowing || Shell.Current is null)
        {
            return;
        }

        _isShowing = true;
        await Shell.Current.Navigation.PushModalAsync(new ForcedPasswordChangePage(), animated: false);
    }

    protected override bool OnBackButtonPressed() => true;

    private async Task CloseAsync()
    {
        _isShowing = false;
        if (Navigation.ModalStack.Count > 0)
        {
            await Navigation.PopModalAsync();
        }
    }
}
