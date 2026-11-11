using CommunityToolkit.Maui.Core;
using LARGA.MobileApp.Controls;
using LARGA.MobileApp.Services;
using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using System;

namespace LARGA.MobileApp.Views.Driver;

public partial class ActiveShiftPage : ContentPage
{
    private const string HoldAnimation = "SosHold";
    private const string ReleaseAnimation = "SosRelease";
    private const uint ReleaseLength = 200;

    // The hold is timed here in whole seconds, not by TouchBehavior's LongPressCommand: the
    // toolkit's long-press timer could survive a short press and fire partway through the next
    // hold (short press, release, hold again → SOS before the ring closed).
    private const int HoldSeconds = 3;

    // Where the hold animation ends, reached exactly when the long press fires.
    private const double MaxButtonScale = 1.15;
    private const double IdleGlowScale = 0.75; // the 80px glow sits hidden behind the 60px button
    private const double MaxGlowScale = 1.1;
    private const double MaxGlowOpacity = 0.6;

    private readonly ActiveShiftViewModel _viewModel;
    private readonly IEmergencyFeedback _feedback;
    private readonly SosHoldRingDrawable _holdRing = new();
    private IDispatcherTimer? _holdTimer;
    private int _holdSecondsElapsed;
    private bool _isHolding;
    private double _holdProgress;

    private readonly EmergencyCountdownCoordinator _countdown;

    public ActiveShiftPage(ActiveShiftViewModel viewModel, IEmergencyFeedback feedback, EmergencyCountdownCoordinator countdown)
    {
        InitializeComponent();
        BindingContext = viewModel;
        _viewModel = viewModel;
        _feedback = feedback;
        _countdown = countdown;
        SosHoldRing.Drawable = _holdRing;
        Unloaded += (_, _) => ResetSosHold();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _countdown.Changed -= OnCountdownChanged;
        _countdown.Changed += OnCountdownChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SyncHostileToast();
        if (Window != null)
        {
            Window.Activated -= OnWindowActivated;
            Window.Activated += OnWindowActivated;
        }
        RestartStandbyTimer();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _countdown.Changed -= OnCountdownChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (Window != null) Window.Activated -= OnWindowActivated;
        HostileCancelToast.IsVisible = false;
        ResetSosHold();
        ExitStandby();
        StopStandbyTimer();
    }

    private void OnCountdownChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            SyncHostileToast();
            if (_countdown.Active != null) ExitStandby();
        });

    // ---- Standby (battery saver) ---------------------------------------------------------
    // Idle on the active screen for StandbyDelay -> black overlay + minimum window brightness.
    // Only the display is reduced: GPS tracking, the foreground service, the safety monitor and
    // the shift timer are untouched, so nothing the manager sees changes.
    private static readonly TimeSpan StandbyDelay = TimeSpan.FromSeconds(30);
    private const float StandbyBrightness = 0.01f;

    private IDispatcherTimer? _standbyTimer;
    private bool _inStandby;

    private bool CanEnterStandby =>
        _viewModel.IsActive
        && !_viewModel.IsPauseAlertVisible && !_viewModel.IsClockOutAlertVisible
        && !_viewModel.IsDuressPinVisible && !_viewModel.IsSosAlertVisible
        && !_isHolding && _countdown.Active == null;

    private void OnUserActivity(object? sender, EventArgs e) => RestartStandbyTimer();

    private void OnStandbyTapped(object? sender, EventArgs e)
    {
        ExitStandby();
        RestartStandbyTimer();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Any dialog, pause or SOS state change counts as activity and wakes the screen.
        switch (e.PropertyName)
        {
            case nameof(ActiveShiftViewModel.IsPaused):
            case nameof(ActiveShiftViewModel.IsPauseAlertVisible):
            case nameof(ActiveShiftViewModel.IsClockOutAlertVisible):
            case nameof(ActiveShiftViewModel.IsDuressPinVisible):
            case nameof(ActiveShiftViewModel.IsSosAlertVisible):
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    ExitStandby();
                    RestartStandbyTimer();
                });
                break;
        }
    }

    private void RestartStandbyTimer()
    {
        StopStandbyTimer();
        _standbyTimer = Dispatcher.CreateTimer();
        _standbyTimer.Interval = StandbyDelay;
        _standbyTimer.IsRepeating = false;
        _standbyTimer.Tick += OnStandbyTimerTick;
        _standbyTimer.Start();
    }

    private void StopStandbyTimer()
    {
        if (_standbyTimer == null) return;
        _standbyTimer.Stop();
        _standbyTimer.Tick -= OnStandbyTimerTick;
        _standbyTimer = null;
    }

    private void OnStandbyTimerTick(object? sender, EventArgs e)
    {
        StopStandbyTimer();
        if (CanEnterStandby) EnterStandby();
        else RestartStandbyTimer();
    }

    private void EnterStandby()
    {
        if (_inStandby) return;
        _inStandby = true;
        StandbyClockLabel.Text = DateTime.Now.ToString("hh:mm tt");
        StandbyOverlay.IsVisible = true;
        SetWindowBrightness(StandbyBrightness);

        // Keep the standby clock current without a fast timer: once a minute is enough. The same
        // tick counts minutes in standby to decide when to hand over to the background.
        _standbyMinutes = 0;
        _standbyTimer = Dispatcher.CreateTimer();
        _standbyTimer.Interval = TimeSpan.FromMinutes(1);
        _standbyTimer.IsRepeating = true;
        _standbyTimer.Tick += OnStandbyMinuteTick;
        _standbyTimer.Start();
    }

    // ---- Stage 2: hand over to the background ----------------------------------------------
    // After StandbyBackgroundAfterMinutes in standby the app moves to the background so the phone's
    // own screen timeout can turn the display fully off. The ShiftTrackingService (foreground
    // service + wake lock + "shift tracking active" notification) keeps GPS and the safety monitor
    // alive; tapping the notification or reopening the app brings the driver straight back.
    // Set to false to keep stage 1 (black screen) only.
    private const bool BackgroundAfterStandby = true;
    private const int StandbyBackgroundAfterMinutes = 5;
    private int _standbyMinutes;

    private void OnStandbyMinuteTick(object? sender, EventArgs e)
    {
        StandbyClockLabel.Text = DateTime.Now.ToString("hh:mm tt");

        if (!BackgroundAfterStandby || ++_standbyMinutes < StandbyBackgroundAfterMinutes) return;

        // Something needing the driver (SOS, countdown, dialog) means stay put.
        if (!CanEnterStandby)
        {
            ExitStandby();
            RestartStandbyTimer();
            return;
        }

        ExitStandby(); // restores the system brightness so the screen timeout behaves normally
        MoveAppToBackground();
    }

    private static void MoveAppToBackground()
    {
#if ANDROID
        try
        {
            Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.MoveTaskToBack(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Move to background failed: {ex.Message}");
        }
#endif
    }

    // Coming back from the background (notification tap, app switcher) starts a fresh idle count.
    private void OnWindowActivated(object? sender, EventArgs e) => RestartStandbyTimer();

    private void ExitStandby()
    {
        if (!_inStandby) return;
        _inStandby = false;
        StandbyOverlay.IsVisible = false;
        SetWindowBrightness(null);
        StopStandbyTimer();
    }

    // null restores the user's own brightness setting.
    private static void SetWindowBrightness(float? level)
    {
#if ANDROID
        try
        {
            var window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
            if (window?.Attributes is { } attrs)
            {
                attrs.ScreenBrightness = level ?? Android.Views.WindowManagerLayoutParams.BrightnessOverrideNone;
                window.Attributes = attrs;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Standby brightness change failed: {ex.Message}");
        }
#endif
    }

    // Hostile only: the Crash countdown has its own full-screen popup.
    private void SyncHostileToast() =>
        HostileCancelToast.IsVisible =
            _countdown.Active is { } active && active.TriggerType == LARGA.Shared.Models.Entities.EmergencyAlert.Hostile;

    private void OnHostileCancelTapped(object? sender, EventArgs e)
    {
        HostileCancelToast.IsVisible = false;
        _countdown.RequestCancel();
    }

    private void OnSosTouchStatusChanged(object? sender, TouchStatusChangedEventArgs e)
    {
        RestartStandbyTimer();
        switch (e.Status)
        {
            case TouchStatus.Started:
                StartSosHold();
                break;

            // Still holding means the 3 seconds weren't up: the finger lifted (or slid off the
            // button) early. After a completed hold the release lands here too and is ignored.
            case TouchStatus.Completed:
            case TouchStatus.Canceled:
                if (_isHolding) CancelSosHold();
                break;
        }
    }

    private void StartSosHold()
    {
        SosButtonHost.AbortAnimation(HoldAnimation);
        SosButtonHost.AbortAnimation(ReleaseAnimation);
        StopHoldTimer();

        _isHolding = true;
        _feedback.HoldStarted();

        // A fresh timer per press, so nothing from an earlier press can count toward this one.
        _holdSecondsElapsed = 0;
        _holdTimer = Dispatcher.CreateTimer();
        _holdTimer.Interval = TimeSpan.FromSeconds(1);
        _holdTimer.IsRepeating = true;
        _holdTimer.Tick += OnHoldTimerTick;
        _holdTimer.Start();

        // Runs for the whole hold so it peaks as the popup opens. It's only the visual: the SOS
        // is sent by the timer, so an animation that ends early (e.g. system animations turned
        // off) can't send it. A press that interrupts a release animation carries on from where
        // that left off, without a jump.
        new Animation(SetSosHoldProgress, _holdProgress, 1, Easing.Linear)
            .Commit(SosButtonHost, HoldAnimation, length: HoldSeconds * 1000);
    }

    private void OnHoldTimerTick(object? sender, EventArgs e)
    {
        // A tick from a timer this press didn't start (already stopped) never counts.
        if (!_isHolding || sender != _holdTimer) return;

        if (++_holdSecondsElapsed < HoldSeconds)
        {
            _feedback.HoldTick();
            return;
        }

        CompleteSosHold();
    }

    // The 3 seconds are up: SendSosCommand opens the SOS popup and gives the press-complete
    // buzz (no tick here, it could replace that buzz with a fainter one). The button goes back
    // to rest.
    private void CompleteSosHold()
    {
        _isHolding = false;
        StopHoldTimer();
        SosButtonHost.AbortAnimation(HoldAnimation);
        SetSosHoldProgress(1);
        AnimateSosRelease();

        if (_viewModel.SendSosCommand.CanExecute(null))
        {
            _viewModel.SendSosCommand.Execute(null);
        }
    }

    private void CancelSosHold()
    {
        _isHolding = false;
        StopHoldTimer();
        _feedback.HoldCancelled();
        AnimateSosRelease();
    }

    private void AnimateSosRelease()
    {
        SosButtonHost.AbortAnimation(HoldAnimation);
        new Animation(SetSosHoldProgress, _holdProgress, 0, Easing.CubicOut)
            .Commit(SosButtonHost, ReleaseAnimation, length: ReleaseLength);
    }

    // Leaving the page mid-hold: drop everything without animating.
    private void ResetSosHold()
    {
        if (_isHolding) _feedback.HoldCancelled();
        _isHolding = false;
        StopHoldTimer();
        SosButtonHost.AbortAnimation(HoldAnimation);
        SosButtonHost.AbortAnimation(ReleaseAnimation);
        SetSosHoldProgress(0);
    }

    private void StopHoldTimer()
    {
        if (_holdTimer == null) return;

        _holdTimer.Stop();
        _holdTimer.Tick -= OnHoldTimerTick;
        _holdTimer = null;
    }

    private void SetSosHoldProgress(double progress)
    {
        _holdProgress = progress;
        SosButtonHost.Scale = 1 + (MaxButtonScale - 1) * progress;
        SosGlow.Scale = IdleGlowScale + (MaxGlowScale - IdleGlowScale) * progress;
        SosGlow.Opacity = MaxGlowOpacity * progress;
        _holdRing.Progress = (float)progress;
        SosHoldRing.Invalidate();
    }

    private async void OnBackButtonClicked(object sender, EventArgs e)
    {
        try
        {
            await LARGA.MobileApp.Services.NavigationGate.GoBackAsync();
        }
        catch (Exception ex)
        {
            // Navigation can fail mid-transition; never let a back tap crash the app.
            System.Diagnostics.Debug.WriteLine($"Back navigation failed: {ex.Message}");
        }
    }
}
