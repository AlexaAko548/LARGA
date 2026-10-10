using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.Services;

/// <summary>
/// The app's one MapTiler reverse geocoder (same key as the map tiles, MapTilerConfig), shared so
/// its address cache is too: the Alert Center and the Live Fleet popup look up the same SOS
/// coordinates. Static because FleetMapViewModel and AlertCenterViewModel aren't built by DI.
/// </summary>
public static class MapTilerGeocoding
{
    public static MapTilerGeocoder Shared { get; } = new(MapTilerConfig.ApiKey);
}
