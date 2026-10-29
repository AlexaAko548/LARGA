using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LARGA.SharedCore;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.ViewModels.Shared;

/// <summary>
/// The fields shared by the manager's and the driver's Change Password screens: the three
/// passwords with their show/hide eyes, the live password-policy checklist
/// (InputValidator.PasswordRules), and whether CONFIRM &amp; SAVE can be pressed yet.
/// </summary>
public abstract class PasswordFormViewModel : BindableObject
{
    private string _currentPassword = string.Empty;
    public string CurrentPassword
    {
        get => _currentPassword;
        set { _currentPassword = value; OnPropertyChanged(); OnFormChanged(); }
    }

    private string _newPassword = string.Empty;
    public string NewPassword
    {
        get => _newPassword;
        set { _newPassword = value; OnPropertyChanged(); OnFormChanged(); }
    }

    private string _confirmNewPassword = string.Empty;
    public string ConfirmNewPassword
    {
        get => _confirmNewPassword;
        set { _confirmNewPassword = value; OnPropertyChanged(); OnFormChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotBusy)); OnPropertyChanged(nameof(CanSave)); }
    }

    public bool IsNotBusy => !IsBusy;

    public PasswordVisibility CurrentPasswordField { get; } = new();
    public PasswordVisibility NewPasswordField { get; } = new();
    public PasswordVisibility ConfirmPasswordField { get; } = new();

    /// <summary>One line per policy rule, ticked as the new password meets it.</summary>
    public IReadOnlyList<PasswordRuleStatus> PasswordRules =>
        InputValidator.PasswordRules.Select(r => new PasswordRuleStatus(r.Description, r.IsMet(NewPassword))).ToList();

    public bool ShowMismatch => ConfirmNewPassword.Length > 0 && ConfirmNewPassword != NewPassword;

    /// <summary>CONFIRM &amp; SAVE lights up once every field is filled, the new password meets the
    /// policy and the confirmation matches.</summary>
    public bool CanSave =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(CurrentPassword)
        && InputValidator.ValidatePassword(NewPassword) is null
        && NewPassword == ConfirmNewPassword;

    /// <summary>Anything typed that leaving the screen would throw away.</summary>
    public bool HasChanges => CurrentPassword.Length > 0 || NewPassword.Length > 0 || ConfirmNewPassword.Length > 0;

    protected void ClearForm()
    {
        CurrentPassword = string.Empty;
        NewPassword = string.Empty;
        ConfirmNewPassword = string.Empty;
    }

    private void OnFormChanged()
    {
        OnPropertyChanged(nameof(PasswordRules));
        OnPropertyChanged(nameof(ShowMismatch));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(HasChanges));
    }
}

/// <summary>Show/hide state of one password field (the eye button beside it).</summary>
public class PasswordVisibility : BindableObject
{
    private bool _isHidden = true;
    public bool IsHidden
    {
        get => _isHidden;
        private set
        {
            _isHidden = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(Description));
        }
    }

    public string Icon => IsHidden ? "eye_icon.png" : "eye_off_icon.png";
    public string Description => IsHidden ? "Show password" : "Hide password";

    public ICommand ToggleCommand { get; }

    public PasswordVisibility()
    {
        ToggleCommand = new Command(() => IsHidden = !IsHidden);
    }
}

public record PasswordRuleStatus(string Text, bool IsMet)
{
    public string Glyph => IsMet ? "✓" : "•";
}

/// <summary>The "leave without saving?" prompt for a form with unsaved edits.</summary>
public static class UnsavedChangesPrompt
{
    /// <summary>True when the user may leave: nothing typed, or they chose to discard it.</summary>
    public static Task<bool> ConfirmLeaveAsync(bool hasChanges) =>
        hasChanges
            ? Shell.Current.DisplayAlert("Discard changes?", "You have unsaved changes. Are you sure you want to go back? Your changes will be lost.", "Discard", "Keep editing")
            : Task.FromResult(true);
}
