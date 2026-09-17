using System.Collections.Specialized;
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

    private readonly FleetMapViewModel _viewModel;
    private readonly MapControl _mapControl;
    private readonly MemoryLayer _pinsLayer;
    private bool _hasCenteredMap;

    public ManagerDashboardPage()
    {
        InitializeComponent();
        _viewModel = new FleetMapViewModel();
        BindingContext = _viewModel;

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

        // Mapsui's Pin isn't bindable-ItemsSource-friendly (no Command/CommandParameter), so
        // the map's feature layer is kept in sync with the viewmodel's Pins by hand.
        _viewModel.Pins.CollectionChanged += OnPinsChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.FleetLoaded += OnFleetLoaded;

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
        _viewModel.LoadFleetCommand.Execute(null);
    }

    private void OnPinsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var features = new List<IFeature>();

        foreach (var pin in _viewModel.Pins)
        {
            var (x, y) = SphericalMercator.FromLonLat(pin.Longitude, pin.Latitude);
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

        if (!_hasCenteredMap && !MapFocusRequest.HasPending && _viewModel.Pins.Count > 0)
        {
            _hasCenteredMap = true;
            var first = _viewModel.Pins[0];
            var (x, y) = SphericalMercator.FromLonLat(first.Longitude, first.Latitude);
            _mapControl.Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), DefaultResolution);
        }
    }

    private void OnFleetLoaded(object? sender, EventArgs e)
    {
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
            // Same as tapping the pin directly - pans the map AND opens its detail sheet.
            _viewModel.SelectPinCommand.Execute(matchingPin);
        }
        else
        {
            // No live pin for this driver (no current shift/telemetry) - still honor the
            // SOS alert's own reported coordinates rather than doing nothing.
            var (x, y) = SphericalMercator.FromLonLat(requestedLon, requestedLat);
            _mapControl.Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), DefaultResolution);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Selecting a unit from the bottom shortcut row can pick a taxi that's off-screen
        // (or was never in view because the map hasn't been panned there) - pan to it same
        // as tapping its pin directly would. A tap on the pin itself also raises this (it's
        // already set before OnMapInfo returns), so this covers both selection paths in one
        // place rather than duplicating the pan logic in OnMapInfo too.
        if (e.PropertyName != nameof(FleetMapViewModel.SelectedPin)) return;

        var pin = _viewModel.SelectedPin;
        if (pin == null) return;

        var (x, y) = SphericalMercator.FromLonLat(pin.Longitude, pin.Latitude);
        _mapControl.Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), DefaultResolution);
    }

    private void OnMapInfo(object? sender, MapInfoEventArgs e)
    {
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
