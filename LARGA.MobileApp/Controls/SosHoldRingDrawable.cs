using Microsoft.Maui.Graphics;

namespace LARGA.MobileApp.Controls;

/// <summary>
/// Progress ring drawn around the SOS button while it's held. <see cref="Progress"/> runs 0 → 1
/// over the hold; the owning GraphicsView is invalidated each frame of the hold animation.
/// </summary>
public sealed class SosHoldRingDrawable : IDrawable
{
    private const float StrokeSize = 4f;

    // Same reds as the SOS button: fill color for the arc, stroke color for the faint track.
    private static readonly Color ArcColor = Color.FromArgb("#FF2424");
    private static readonly Color TrackColor = Color.FromArgb("#FF7373").WithAlpha(0.35f);

    public float Progress { get; set; }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (Progress <= 0f) return;

        RectF ring = dirtyRect.Inflate(-StrokeSize / 2f, -StrokeSize / 2f);
        canvas.StrokeSize = StrokeSize;
        canvas.StrokeLineCap = LineCap.Round;

        canvas.StrokeColor = TrackColor;
        canvas.DrawEllipse(ring);

        canvas.StrokeColor = ArcColor;
        if (Progress >= 0.999f)
        {
            // A full 360° arc has equal start and end angles and draws nothing on some platforms.
            canvas.DrawEllipse(ring);
            return;
        }

        // Angles are in degrees, 0 = 3 o'clock, counter-clockwise positive: start at 12 o'clock
        // and sweep clockwise.
        canvas.DrawArc(ring.X, ring.Y, ring.Width, ring.Height, 90f, 90f - 360f * Progress, true, false);
    }
}
