using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Controls;

public partial class OptionPickerPopup : Popup
{
    public OptionPickerPopup(string title, IReadOnlyList<string> options, int selectedIndex)
    {
        InitializeComponent();
        TitleLabel.Text = title;

        for (int i = 0; i < options.Count; i++)
        {
            OptionsLayout.Children.Add(BuildRow(options[i], i, i == selectedIndex));
        }
    }

    // One option: light-blue row like Ledger.Field, or primary blue with a check mark when it's the current choice.
    private Border BuildRow(string text, int index, bool isSelected)
    {
        var label = new Label
        {
            Text = text,
            FontFamily = "InterRegular",
            FontAttributes = FontAttributes.Bold,
            FontSize = 15,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        label.SetDynamicResource(Label.TextColorProperty, isSelected ? "LargaWhite" : "LargaDarkText");

        var check = new Label
        {
            Text = "", // Material "check"
            FontFamily = "MaterialIcons",
            FontSize = 20,
            VerticalOptions = LayoutOptions.Center,
            IsVisible = isSelected,
        };
        check.SetDynamicResource(Label.TextColorProperty, "LargaWhite");

        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 10 };
        grid.Add(label, 0);
        grid.Add(check, 1);

        var row = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(14, 12),
            Content = grid,
        };
        row.SetDynamicResource(VisualElement.BackgroundColorProperty, isSelected ? "LargaPrimaryBlue" : "LargaLightBlue");
        row.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Close(index)) });
        return row;
    }

    private void OnCancelClicked(object? sender, EventArgs e) => Close(null);

    /// <summary>
    /// Shows the list over <paramref name="page"/> (pass Shell.Current) and returns the tapped option's index, or null
    /// for Cancel, tapping outside, or the hardware back button.
    /// </summary>
    public static async Task<int?> ShowAsync(Page page, string title, IReadOnlyList<string> options, int selectedIndex)
    {
        object? result = await page.ShowPopupAsync(new OptionPickerPopup(title, options, selectedIndex));
        return result as int?;
    }
}
