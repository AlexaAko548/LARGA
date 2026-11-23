using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Controls;

/// <summary>Format (CSV/PDF) and date range chosen on the Export popup - default range is the last 7 days,
/// same as the rest of the app's exports (ManagerWeb's Generate Reports).</summary>
public sealed record ExportOptionsResult(bool IsPdf, DateTime FromDate, DateTime ToDate);

public partial class ExportOptionsPopup : Popup
{
    private bool _isPdf;

    public ExportOptionsPopup()
    {
        InitializeComponent();
        FromPicker.Date = DateTime.Today.AddDays(-7);
        ToPicker.Date = DateTime.Today;
        ApplyFormatSelection();
    }

    private void OnCsvTapped(object? sender, EventArgs e)
    {
        _isPdf = false;
        ApplyFormatSelection();
    }

    private void OnPdfTapped(object? sender, EventArgs e)
    {
        _isPdf = true;
        ApplyFormatSelection();
    }

    private void ApplyFormatSelection()
    {
        CsvOption.SetDynamicResource(VisualElement.BackgroundColorProperty, _isPdf ? "LargaLightBlue" : "LargaPrimaryBlue");
        PdfOption.SetDynamicResource(VisualElement.BackgroundColorProperty, _isPdf ? "LargaPrimaryBlue" : "LargaLightBlue");
        CsvLabel.SetDynamicResource(Label.TextColorProperty, _isPdf ? "LargaDarkText" : "LargaWhite");
        PdfLabel.SetDynamicResource(Label.TextColorProperty, _isPdf ? "LargaWhite" : "LargaDarkText");
    }

    private void OnDateSelected(object? sender, DateChangedEventArgs e) => ErrorLabel.IsVisible = false;

    private void OnCancelClicked(object? sender, EventArgs e) => Close(null);

    private void OnExportClicked(object? sender, EventArgs e)
    {
        DateTime from = FromPicker.Date;
        DateTime to = ToPicker.Date;

        if (from > to)
        {
            ErrorLabel.Text = "The From date must be on or before the To date.";
            ErrorLabel.IsVisible = true;
            return;
        }

        if ((to - from).TotalDays > 366)
        {
            ErrorLabel.Text = "Pick a range of one year or less.";
            ErrorLabel.IsVisible = true;
            return;
        }

        Close(new ExportOptionsResult(_isPdf, from, to));
    }

    /// <summary>Shows the popup over <paramref name="page"/> (pass Shell.Current) and returns the chosen
    /// format + range, or null for Cancel, tapping outside, or the hardware back button.</summary>
    public static async Task<ExportOptionsResult?> ShowAsync(Page page)
    {
        object? result = await page.ShowPopupAsync(new ExportOptionsPopup());
        return result as ExportOptionsResult;
    }
}
