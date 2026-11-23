using CommunityToolkit.Maui.Views;
using LARGA.MobileApp.Services;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace LARGA.MobileApp.Controls;

public partial class ConfirmationPopup : Popup
{
    public ConfirmationPopup(string title, string message, string confirmText, string cancelText, bool isDangerous)
    {
        InitializeComponent();
        TitleLabel.Text = title;
        MessageLabel.Text = message;
        ConfirmButton.Text = confirmText;
        CancelButton.Text = cancelText;
        ConfirmButton.BackgroundColor = isDangerous
            ? ThemeColors.Get("LargaDanger", Color.FromArgb("#D33F3F"))
            : ThemeColors.Get("LargaPrimaryBlue", Color.FromArgb("#019BCF"));
    }

    private void OnCancelClicked(object? sender, EventArgs e) => Close(false);

    private void OnConfirmClicked(object? sender, EventArgs e) => Close(true);

    /// <summary>
    /// Shows the popup over <paramref name="page"/> (pass Shell.Current) and returns true only if
    /// Confirm was tapped - false for Cancel, tapping outside, or the hardware back button.
    /// </summary>
    public static async Task<bool> ShowAsync(Page page, string title, string message,
        string confirmText = "Confirm", string cancelText = "Cancel", bool isDangerous = true)
    {
        var popup = new ConfirmationPopup(title, message, confirmText, cancelText, isDangerous);
        object? result = await page.ShowPopupAsync(popup);
        return result is true;
    }
}
