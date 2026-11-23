using System;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace LARGA.MobileApp.Views.Shared;

/// <summary>
/// A single photo, full screen: opened from a clock-in approval card in the Alert Center so the manager
/// (and the assistant manager) can review the inspection photos on the phone, as the web already lets them.
/// </summary>
public partial class PhotoViewerPage : ContentPage
{
    private readonly Uri? _uri;

    public PhotoViewerPage(string title, string url)
    {
        InitializeComponent();
        TitleLabel.Text = string.IsNullOrWhiteSpace(title) ? "Photo" : title;

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            _uri = uri;
            Photo.Source = ImageSource.FromUri(uri);
        }
    }

    private async void OnCloseClicked(object? sender, EventArgs e)
    {
        try
        {
            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Closing the photo viewer failed: {ex.Message}");
        }
    }

    private async void OnOpenFullSizeClicked(object? sender, EventArgs e)
    {
        if (_uri is null) return;

        try
        {
            await Launcher.Default.OpenAsync(_uri);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Opening the photo in the browser failed: {ex.Message}");
            await DisplayAlert("Couldn't open the photo", "Please check your connection and try again.", "OK");
        }
    }
}
