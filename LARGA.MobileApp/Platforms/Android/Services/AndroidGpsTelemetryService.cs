using System;
using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.Platforms.Android.Services;

/// <summary>
/// GPS-012: the Android IGpsTelemetryService. Tracking itself is the shared GpsTelemetryService
/// (same 30-second writes, break pause and LAR-77 auto-cutoff); this only runs
/// <see cref="ShiftTrackingService"/> for exactly as long as it tracks a shift, so the loop keeps
/// going after the app is swiped away. Every caller (clock-in, dashboard resume, clock-out, logout)
/// goes through this interface, so none of them change.
/// </summary>
public sealed class AndroidGpsTelemetryService : IGpsTelemetryService
{
    private readonly GpsTelemetryService _inner;

    public AndroidGpsTelemetryService(GpsTelemetryService inner)
    {
        _inner = inner;

        // Closed elsewhere (6:00 AM auto-close, a manager): the loop has stopped itself - the
        // foreground service and its notification go with it.
        _inner.ShiftClosedRemotely += (_, _) => ShiftTrackingService.Stop(global::Android.App.Application.Context);
    }

    public void Start(string shiftId)
    {
        _inner.Start(shiftId);
        if (!string.IsNullOrWhiteSpace(shiftId))
        {
            ShiftTrackingService.Start(global::Android.App.Application.Context, shiftId);
        }
    }

    public void Stop()
    {
        _inner.Stop();
        ShiftTrackingService.Stop(global::Android.App.Application.Context);
    }

    public double CurrentDistanceKm => _inner.CurrentDistanceKm;

    public bool IsMoving => _inner.IsMoving;

    public event EventHandler<double>? DistanceChanged
    {
        add => _inner.DistanceChanged += value;
        remove => _inner.DistanceChanged -= value;
    }

    public event EventHandler? MovementChanged
    {
        add => _inner.MovementChanged += value;
        remove => _inner.MovementChanged -= value;
    }

    public event EventHandler<string>? ShiftClosedRemotely
    {
        add => _inner.ShiftClosedRemotely += value;
        remove => _inner.ShiftClosedRemotely -= value;
    }
}
