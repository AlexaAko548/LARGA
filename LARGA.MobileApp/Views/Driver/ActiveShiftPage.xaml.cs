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

    public ActiveShiftPage(ActiveShiftViewModel viewModel, IEmergencyFeedback feedback)
    {
        InitializeComponent();
        BindingContext = viewModel;
        _viewModel = viewModel;
        _feedback = feedback;
        SosHoldRing.Drawable = _holdRing;
        Unloaded += (_, _) => ResetSosHold();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        ResetSosHold();
    }

    private void OnSosTouchStatusChanged(object? sender, TouchStatusChangedEventArgs e)
    {
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
            await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            // Navigation can fail mid-transition; never let a back tap crash the app.
            System.Diagnostics.Debug.WriteLine($"Back navigation failed: {ex.Message}");
        }
    }
}
