using System;
using LARGA.MobileApp.Services;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Graphics;

namespace LARGA.MobileApp.Controls;

/// <summary>
/// LAR-117: drawn over every page of the window (a MAUI window overlay), so a Firestore load on any
/// screen shows activity without that screen's layout changing: a thin sliding bar along the top
/// and a "Loading…" pill with a spinner near the bottom. Taps pass straight through to the page.
/// Appears only after a short grace period, so fast loads don't flash it.
/// </summary>
public sealed class LoadingWindowOverlay : WindowOverlay, IWindowOverlayElement
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);
    private static readonly Color Accent = Color.FromArgb("#019BCF");

    private readonly IDispatcherTimer _timer;
    private DateTime _busySinceUtc;
    private float _phase;

    public LoadingWindowOverlay(IWindow window) : base(window)
    {
        AddWindowElement(this);
        _timer = Dispatcher.GetForCurrentThread()!.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += (_, _) =>
        {
            _phase = (_phase + 0.025f) % 1f;
            Invalidate();
        };
        LoadingState.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(SyncWithState);
        SyncWithState();
    }

    private bool ShouldShow => LoadingState.IsBusy && DateTime.UtcNow - _busySinceUtc >= Grace;

    private void SyncWithState()
    {
        if (LoadingState.IsBusy)
        {
            if (!_timer.IsRunning)
            {
                _busySinceUtc = DateTime.UtcNow;
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
            Invalidate();
        }
    }

    // Never claims a touch - the page underneath stays fully usable.
    public bool Contains(Point point) => false;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (!ShouldShow)
        {
            return;
        }

        float width = dirtyRect.Width;

        // Top bar: a faint track with a segment sliding across it.
        canvas.FillColor = Accent.WithAlpha(0.18f);
        canvas.FillRectangle(0, 0, width, 3);
        float segment = width * 0.4f;
        float x = -segment + (width + segment) * _phase;
        canvas.FillColor = Accent;
        canvas.FillRoundedRectangle(x, 0, segment, 3, 1.5f);

        // Pill near the bottom, above the tab bar.
        const float pillWidth = 132, pillHeight = 36;
        float pillX = (width - pillWidth) / 2;
        float pillY = dirtyRect.Height - pillHeight - 110;
        canvas.FillColor = Color.FromArgb("#E60F2A3D");
        canvas.FillRoundedRectangle(pillX, pillY, pillWidth, pillHeight, pillHeight / 2);

        // Spinner: a rotating arc.
        float cx = pillX + 22, cy = pillY + pillHeight / 2, r = 8;
        canvas.StrokeSize = 2.5f;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeColor = Colors.White.WithAlpha(0.25f);
        canvas.DrawEllipse(cx - r, cy - r, r * 2, r * 2);
        canvas.StrokeColor = Colors.White;
        float start = 360f * _phase * 3f;
        canvas.DrawArc(cx - r, cy - r, r * 2, r * 2, start, start + 100, false, false);

        canvas.FontColor = Colors.White;
        canvas.FontSize = 14;
        canvas.DrawString("Loading…", pillX + 38, pillY, pillWidth - 46, pillHeight, HorizontalAlignment.Left, VerticalAlignment.Center);
    }
}
