using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic;

namespace LARGA.MobileApp.Views.Driver;

public partial class OdometerScanPage : ContentPage
{
    public OdometerScanPage()
    {
        InitializeComponent();
        SimulateMLKitScan();
    }

    private async void SimulateMLKitScan()
    {
        // Simulates the delay of Google ML Kit finding the numbers in the camera frame
        await System.Threading.Tasks.Task.Delay(1500);
        DetectedTextLabel.Text = "48201";
    }

    private async void OnCaptureClicked(object sender, EventArgs e)
    {
        // Passes the scanned text back to PreShiftStep2ViewModel
        var navigationParameter = new Dictionary<string, object>
        {
            { "ScannedOdometer", DetectedTextLabel.Text }
        };

        await Shell.Current.GoToAsync("..", navigationParameter);
    }
}