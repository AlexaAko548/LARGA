using System;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using LARGA.MobileApp.Services;
using LARGA.Shared.Models.Entities;

namespace LARGA.MobileApp.Views.Shared;

/// <summary>
/// LAR-86/87: the 5-second cancel window of an automated SOS, shown modally over whatever the
/// driver is doing (App pushes it when the countdown starts). Cancel tells the detection service
/// to drop the alert; the page closes itself when the countdown ends either way.
/// </summary>
public partial class EmergencyCountdownPage : ContentPage
{
    private readonly EmergencyCountdownCoordinator _coordinator;
    private readonly IDispatcherTimer _timer;

    public EmergencyCountdownPage(EmergencyCountdownCoordinator coordinator)
    {
        InitializeComponent();
        _coordinator = coordinator;

        bool crash = _coordinator.Active?.TriggerType == EmergencyAlert.Crash;
        TitleLabel.Text = crash ? "ACCIDENT DETECTED" : "SENDING SILENT SOS";
        MessageLabel.Text = crash
            ? "Your manager will be alerted. Tap Cancel if you're OK."
            : "A hostile-situation alert will be sent to your manager. Tap Cancel if this was a mistake.";

        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick += (_, _) => Refresh();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh();
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        _timer.Stop();
        base.OnDisappearing();
    }

    // The hardware back button must not dismiss the countdown silently - only Cancel stops it.
    protected override bool OnBackButtonPressed() => true;

    private void Refresh()
    {
        if (_coordinator.Active is not { } active)
        {
            return; // App closes the page when the countdown ends
        }

        double remaining = (active.EndsAtUtc - DateTime.UtcNow).TotalSeconds;
        SecondsLabel.Text = Math.Max(0, (int)Math.Ceiling(remaining)).ToString();
    }

    private void OnCancelClicked(object? sender, EventArgs e) => _coordinator.RequestCancel();
}
