using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace LARGA.MobileApp.ViewModels;

public class ForgotPasswordViewModel : INotifyPropertyChanged
{
    private string _email = string.Empty;
    private string _code = string.Empty;
    private string _newPassword = string.Empty;
    private string _confirmNewPassword = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Email
    {
        get => _email;
        set => SetProperty(ref _email, value);
    }

    public string Code
    {
        get => _code;
        set => SetProperty(ref _code, value);
    }

    public string NewPassword
    {
        get => _newPassword;
        set => SetProperty(ref _newPassword, value);
    }

    public string ConfirmNewPassword
    {
        get => _confirmNewPassword;
        set => SetProperty(ref _confirmNewPassword, value);
    }

    public ICommand SendEmailCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand ResetPasswordCommand { get; }

    public ForgotPasswordViewModel()
    {
        SendEmailCommand = new Command(async () => await OnSendEmailAsync());
        VerifyCommand = new Command(async () => await OnVerifyAsync());
        ResetPasswordCommand = new Command(async () => await OnResetPasswordAsync());
    }

    private async Task OnSendEmailAsync()
    {
        // Navigate to verify page
        await Shell.Current.GoToAsync("forgot-password-verify");
    }

    private async Task OnVerifyAsync()
    {
        // Navigate to reset password page
        await Shell.Current.GoToAsync("forgot-password-new");
    }

    private async Task OnResetPasswordAsync()
    {
        // Go back to login
        await Shell.Current.GoToAsync("///login");
    }

    protected void SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string propertyName = "")
    {
        if (Equals(backingStore, value)) return;
        backingStore = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
