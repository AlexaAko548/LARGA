using System.ComponentModel;
using BruTile.Predefined;
using BruTile.Web;
using LARGA.MobileApp.Services;
using LARGA.MobileApp.ViewModels.Manager;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using Mapsui.UI.Maui;
using MColor = Mapsui.Styles.Color;
using MBrush = Mapsui.Styles.Brush;
using MFont = Mapsui.Styles.Font;
using NtsPoint = NetTopologySuite.Geometries.Point;

namespace LARGA.MobileApp.Views.Manager;

public partial class ManagerDashboardPage : ContentPage
{
    // Talisay City, Cebu - LARGA's base of operations. Shown until real fleet pins load.
    private const double DefaultLon = 123.8494;
    private const double DefaultLat = 10.2447;

    // ~zoom level 15 (neighborhood/street level) - MapTiler's road styling and labels only
    // get bold and legible once you're this close in; the old city-wide default (resolution
    // 20, ~zoom 13) left roads thin and washed out.
    private const double DefaultResolution = 4.8;

    // ~zoom level 18 (building/street-address level) - the "Navigate" button's whole point is
    // to zoom in past the general fleet-overview level to the driver's exact spot.
    private const double CloseUpResolution = 0.6;

    // How long a pin glides to *correct* onto a freshly-refreshed real position, instead of
    // snapping instantly - the write cadence underneath (gps_telemetry + the shift doc's
    // denormalized currentLatitude/Longitude, see TelemetryServices.cs) stays at 30s; this is
    // a purely visual smoothing layer. Between real updates, dead reckoning (below) takes over
    // so the pin doesn't just sit frozen for the other ~28s of the gap.
    private const uint PinGlideDurationMs = 1500;
    private const string PinAnimationName = "FleetPinGlide";

    // How far past a real fix's timestamp dead reckoning is willing to keep projecting the pin
    // forward - 1.5x the telemetry cadence, so a brief network hiccup doesn't visibly stall the
    // pin. Past that the driver has likely gone quiet (killed app, lost signal), so the pin eases
    // back onto its last *real* fix over StaleReturnSeconds and stays there, rather than being
    // left parked at a guessed spot hundreds of meters down the road.
    private const double MaxDeadReckoningSeconds = 45;
    private const double StaleReturnSeconds = 5;
    private const int DeadReckoningTickMs = 200;

    private readonly FleetMapViewModel _viewModel;
    private MapControl? _mapControl;
    private MemoryLayer? _pinsLayer;
    private bool _hasCenteredMap;

    // Each entry is the last real (not projected) fix for that taxi - the anchor dead
    // reckoning projects forward from, and the glide animation corrects onto when a newer one
    // arrives.
    private readonly Dictionary<string, FleetPin> _liveAnchors = new();
    private IDispatcherTimer? _deadReckoningTimer;

    public ManagerDashboardPage()
    {
        InitializeComponent();
        _viewModel = new FleetMapViewModel();
        BindingContext = _viewModel;

        // LAR-86/87: make sure this manager's number is one a driver's phone will auto-answer
        // after an automated SOS. Covers fresh logins and remember-me alike.
        _ = ManagerAllowlistSync.SyncCurrentManagerAsync();

        // SkiaSharp's WinUI native interop (which Mapsui's MapRenderer depends on) is broken
        // in *unpackaged* Windows builds - a known, currently-unresolved upstream limitation
        // (see dotnet/maui#23737, mono/SkiaSharp#2968/#3440), not anything specific to this
        // app. This project deliberately builds unpackaged on Windows
        // (WindowsPackageType=None in the .csproj) so the team can debug UI quickly without
        // MSIX signing/packaging - so on Windows, `new MapControl()` throws a
        // TypeInitializationException the moment it's touched, taking down the whole page
        // with it. Android (where this actually ships) is unaffected. Rather than crash,
        // degrade: catch it here and show a placeholder instead of the live map, so the rest
        // of the Manager Dashboard (which doesn't depend on the map) still works on Windows.
        try
        {
            _mapControl = new MapControl();
            MapHost.Content = _mapControl;

            var tileSource = new HttpTileSource(
                new GlobalSphericalMercator(),
                $"https://api.maptiler.com/maps/streets-v2/{{z}}/{{x}}/{{y}}.png?key={MapTilerConfig.ApiKey}",
                name: "MapTiler");
            _mapControl.Map.Layers.Add(new TileLayer(tileSource));

            _pinsLayer = new MemoryLayer("FleetPins") { Features = [] };
            _mapControl.Map.Layers.Add(_pinsLayer);

            _mapControl.Info += OnMapInfo;

            var (defaultX, defaultY) = SphericalMercator.FromLonLat(DefaultLon, DefaultLat);
            _mapControl.Map.Navigator.CenterOnAndZoomTo(new MPoint(defaultX, defaultY), DefaultResolution);

            // Mapsui's Pin isn't bindable-ItemsSource-friendly (no Command/CommandParameter),
            // so the map's feature layer is kept in sync with the viewmodel's Pins by hand.
            // Driven off FleetLoaded (fires once, after a full refresh settles) rather than
            // Pins.CollectionChanged (fires once per Clear() and once per Add(), mid-rebuild)
            // so a glide animation always starts from a stable prior frame, never a partial one.
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.FleetLoaded += OnFleetLoaded;
            _viewModel.NavigateRequested += OnNavigateRequested;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Live Fleet Map unavailable on this platform/build: {ex.Message}");
            _mapControl = null;
            _pinsLayer = null;
            MapHost.Content = new Label
            {
                Text = "Live map unavailable in this build.\n(Known SkiaSharp/Windows-unpackaged limitation - works on Android.)",
                TextColor = Colors.White,
                BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#0F2A3D"),
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
                Padding = new Thickness(24),
            };
        }

        // Keep the map border below the header instead of a fixed guessed margin - the
        // header's height changes with its content (e.g. the date label) and with OS font
        // scaling, so a hardcoded top margin drifts out of sync and starts overlapping.
        HeaderStack.SizeChanged += OnHeaderSizeChanged;
    }

    private void OnHeaderSizeChanged(object? sender, EventArgs e)
    {
        if (HeaderStack.Height <= 0) return;
        var current = MapBorder.Margin;
        MapBorder.Margin = new Thickness(current.Left, HeaderStack.Height + 16, current.Right, current.Bottom);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.StartListening();

        if (_mapControl != null)
        {
            _deadReckoningTimer ??= Dispatcher.CreateTimer();
            _deadReckoningTimer.Interval = TimeSpan.FromMilliseconds(DeadReckoningTickMs);
            _deadReckoningTimer.Tick -= OnDeadReckoningTick;
            _deadReckoningTimer.Tick += OnDeadReckoningTick;
            _deadReckoningTimer.Start();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.StopListening();
        _deadReckoningTimer?.Stop();
        this.AbortAnimation(PinAnimationName);
        _liveAnchors.Clear();
    }

    /// <summary>Rebuilds the pin layer from the viewmodel's current Pins. A true data refresh
    /// (animate: true, from FleetLoaded) corrects any pin whose real position actually changed
    /// - gliding from wherever it's currently displayed (which may itself be a dead-reckoned
    /// guess, not the old real fix - see CurrentDisplayPosition) onto the new real one; a
    /// filter-only change (animate: false, from the StatusFilter property) just snaps the
    /// now-visible set straight to their real positions, nothing to glide from.</summary>
    private void RenderPins(bool animate)
    {
        if (_mapControl is null || _pinsLayer is null) return;

        var now = DateTime.UtcNow;
        var animating = new Dictionary<string, (double FromX, double FromY, double ToX, double ToY)>();

        if (animate)
        {
            foreach (var pin in _viewModel.Pins)
            {
                if (!_liveAnchors.TryGetValue(pin.TaxiId, out var prevAnchor)) continue;
                if (prevAnchor.PositionTimestamp == pin.PositionTimestamp) continue; // same fix, nothing changed

                var (fromX, fromY) = CurrentDisplayPosition(prevAnchor, now);
                var (toX, toY) = AnchorXY(pin);
                if (Math.Abs(fromX - toX) > 0.01 || Math.Abs(fromY - toY) > 0.01)
                {
                    animating[pin.TaxiId] = (fromX, fromY, toX, toY);
                }
            }
        }

        _liveAnchors.Clear();
        foreach (var pin in _viewModel.Pins) _liveAnchors[pin.TaxiId] = pin;

        this.AbortAnimation(PinAnimationName);

        if (animating.Count == 0)
        {
            ApplyPinFeatures(_viewModel.Pins.Select(AnchorPosition));
        }
        else
        {
            var targets = _viewModel.Pins.ToList();
            var anim = new Animation(t =>
            {
                var frame = targets.Select(pin => animating.TryGetValue(pin.TaxiId, out var a)
                    ? (pin, X: a.FromX + (a.ToX - a.FromX) * t, Y: a.FromY + (a.ToY - a.FromY) * t)
                    : AnchorPosition(pin));
                ApplyPinFeatures(frame);
            });
            anim.Commit(this, PinAnimationName, length: PinGlideDurationMs, easing: Easing.CubicInOut);
        }

        if (!_hasCenteredMap && !MapFocusRequest.HasPending && _viewModel.Pins.Count > 0)
        {
            _hasCenteredMap = true;
            var (cx, cy) = AnchorXY(_viewModel.Pins[0]);
            _mapControl.Map.Navigator.CenterOnAndZoomTo(new MPoint(cx, cy), DefaultResolution);
        }
    }

    /// <summary>Projects a pin's position forward from its last real fix using its reported
    /// speed/heading for up to MaxDeadReckoningSeconds, then eases it back onto the real fix
    /// (see StaleReturnSeconds) so a driver who's gone quiet isn't left shown somewhere they
    /// never actually were. A stationary pin (speed 0), or one whose phone reported no heading
    /// (negative - see GpsTelemetryService), just returns its real fix unchanged rather than
    /// guessing a direction.</summary>
    private static (double X, double Y) CurrentDisplayPosition(FleetPin pin, DateTime now)
    {
        var (anchorX, anchorY) = AnchorXY(pin);
        if (pin.SpeedKmh <= 0 || pin.HeadingDegrees < 0) return (anchorX, anchorY);

        var rawElapsedSeconds = (now - pin.PositionTimestamp).TotalSeconds;
        var elapsedSeconds = Math.Clamp(rawElapsedSeconds, 0, MaxDeadReckoningSeconds);
        var distanceMeters = (pin.SpeedKmh / 3.6) * elapsedSeconds;
        var headingRadians = pin.HeadingDegrees * Math.PI / 180.0;

        // Compass bearing (0deg = north = +Y, 90deg = east = +X) maps directly onto
        // SphericalMercator's meters-based axes - close enough at city scale that the small
        // Mercator distortion doesn't matter for a visual approximation like this.
        var projectedX = anchorX + distanceMeters * Math.Sin(headingRadians);
        var projectedY = anchorY + distanceMeters * Math.Cos(headingRadians);

        if (rawElapsedSeconds <= MaxDeadReckoningSeconds) return (projectedX, projectedY);

        // Gone stale: slide from the furthest projected point back to the real fix, then hold.
        var t = Math.Min((rawElapsedSeconds - MaxDeadReckoningSeconds) / StaleReturnSeconds, 1);
        return (projectedX + (anchorX - projectedX) * t, projectedY + (anchorY - projectedY) * t);
    }

    /// <summary>Where a pin is currently drawn (dead-reckoned), using the latest real fix the
    /// page holds for that taxi - SelectedPin can be an older FleetPin instance from before the
    /// last refresh. Used for camera moves so zooming onto a driver lands on the pin actually on
    /// screen, not on a fix that may already be a few hundred meters behind it.</summary>
    private (double X, double Y) DisplayedXY(FleetPin pin)
    {
        var latest = _liveAnchors.TryGetValue(pin.TaxiId, out var anchor) ? anchor : pin;
        return CurrentDisplayPosition(latest, DateTime.UtcNow);
    }

    private static (double X, double Y) AnchorXY(FleetPin pin) => SphericalMercator.FromLonLat(pin.Longitude, pin.Latitude);

    private static (FleetPin Pin, double X, double Y) AnchorPosition(FleetPin pin)
    {
        var (x, y) = AnchorXY(pin);
        return (pin, x, y);
    }

    /// <summary>Ticks every 200ms while the Map tab is visible, nudging each pin forward along
    /// its last known heading/speed so a moving unit visibly creeps between the ~30s real
    /// updates instead of sitting frozen - the Foodpanda/Grab-style "live" look. Paused while a
    /// glide correction (RenderPins) is actively running so the two don't fight over the same
    /// frame.</summary>
    private void OnDeadReckoningTick(object? sender, EventArgs e)
    {
        if (_mapControl is null || _pinsLayer is null || _liveAnchors.Count == 0) return;
        if (this.AnimationIsRunning(PinAnimationName)) return;

        var now = DateTime.UtcNow;
        var frame = _liveAnchors.Values.Select(pin =>
        {
            var (x, y) = CurrentDisplayPosition(pin, now);
            return (pin, x, y);
        });
        ApplyPinFeatures(frame);
    }

    private void ApplyPinFeatures(IEnumerable<(FleetPin Pin, double X, double Y)> items)
    {
        if (_mapControl is null || _pinsLayer is null) return;

        var features = new List<IFeature>();

        foreach (var (pin, x, y) in items)
        {
            var digits = new string(pin.TaxiId.Where(char.IsDigit).ToArray());
            var shortUnitLabel = int.TryParse(digits, out var unitNumber) ? $"{unitNumber:D2}" : "—";

            var feature = new GeometryFeature(new NtsPoint(x, y))
            {
                Data = pin,
                Styles = new List<IStyle>
                {
                    new SymbolStyle
                    {
                        SymbolType = SymbolType.Ellipse,
                        SymbolScale = 0.9,
                        Fill = new MBrush(StatusColor(pin.Status)),
                        Outline = new Pen(MColor.White, 2),
                    },
                    // Small unit-number badge under each pin, matching the shared mockup.
                    new LabelStyle
                    {
                        Text = shortUnitLabel,
                        Font = new MFont { Size = 11, Bold = true },
                        ForeColor = MColor.White,
                        BackColor = new MBrush(new MColor(15, 42, 61)), // LargaNavy
                        CornerRounding = 6,
                        Offset = new Offset(0, 16),
                    },
                },
            };
            features.Add(feature);
        }

        _pinsLayer.Features = features;
        _mapControl.RefreshGraphics();
    }

    private void OnNavigateRequested(object? sender, FleetPin pin)
    {
        if (_mapControl is null) return;

        var (x, y) = DisplayedXY(pin);
        _mapControl.Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), CloseUpResolution);
    }

    private void OnFleetLoaded(object? sender, EventArgs e)
    {
        if (_mapControl is null) return;

        RenderPins(animate: true);

        // Runs once per load, after Pins is fully rebuilt (unlike Pins.CollectionChanged,
        // which fires separately for ApplyFilter's Clear() and each individual Add() - acting
        // on one of those mid-rebuild events could see a partial/empty list and wrongly
        // conclude the target driver has no pin).
        if (!MapFocusRequest.TryConsume(out var requestedDriverId, out var requestedLat, out var requestedLon))
        {
            return;
        }

        _hasCenteredMap = true;

        // A status filter left on from earlier could hide the very pin being jumped to.
        _viewModel.StatusFilter = null;

        var matchingPin = requestedDriverId != null
            ? _viewModel.Pins.FirstOrDefault(p => p.DriverId == requestedDriverId)
            : null;

        if (matchingPin != null)
        {
            // Opens the detail sheet, same as tapping the pin directly. This also pans the
            // map at DefaultResolution (see OnViewModelPropertyChanged) - immediately
            // overridden below with a tight zoom, same as the Navigate button, since jumping
            // here from Alert Center should land right on the driver, not just in view of it.
            _viewModel.SelectPinCommand.Execute(matchingPin);
        }

        // No live pin for this driver (no current shift/telemetry) still gets honored via the
        // SOS alert's own reported coordinates, rather than doing nothing.
        var (x, y) = matchingPin != null
            ? DisplayedXY(matchingPin)
            : SphericalMercator.FromLonLat(requestedLon, requestedLat);
        _mapControl.Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), CloseUpResolution);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FleetMapViewModel.StatusFilter))
        {
            // Filter toggled: the visible set changed, but no new telemetry arrived, so just
            // snap to the (already-known) real positions - nothing to glide from.
            RenderPins(animate: false);
            return;
        }

        // Selecting a unit from the bottom shortcut row can pick a taxi that's off-screen
        // (or was never in view because the map hasn't been panned there) - pan to it same
        // as tapping its pin directly would. A tap on the pin itself also raises this (it's
        // already set before OnMapInfo returns), so this covers both selection paths in one
        // place rather than duplicating the pan logic in OnMapInfo too.
        if (e.PropertyName != nameof(FleetMapViewModel.SelectedPin)) return;
        if (_mapControl is null) return;

        var pin = _viewModel.SelectedPin;
        if (pin == null) return;

        var (x, y) = DisplayedXY(pin);
        _mapControl.Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), DefaultResolution);
    }

    private void OnMapInfo(object? sender, MapInfoEventArgs e)
    {
        if (_pinsLayer is null) return;

        var mapInfo = e.GetMapInfo([_pinsLayer]);
        if (mapInfo.Feature?.Data is FleetPin pin)
        {
            _viewModel.SelectPinCommand.Execute(pin);
        }
    }

    private static MColor StatusColor(FleetDriverStatus status) => status switch
    {
        FleetDriverStatus.Active => new MColor(30, 142, 90),
        FleetDriverStatus.OnBreak => new MColor(201, 122, 27),
        FleetDriverStatus.Sos => new MColor(211, 63, 63),
        _ => new MColor(107, 128, 138),
    };
}
