namespace LARGA.MobileApp.Services;

/// <summary>
/// Simple in-memory handoff so a tap on Alert Center's SOS "car" icon can tell the Map tab
/// which driver to center on and select, without AlertCenterViewModel and FleetMapViewModel
/// (independent tabs under the same Shell TabBar) needing a direct reference to each other.
/// Set once by the requester, read and cleared once by the Map page - stale requests never
/// linger to affect a later, unrelated visit to the Map tab.
/// </summary>
public static class MapFocusRequest
{
    private static string? _driverId;
    private static double? _latitude;
    private static double? _longitude;

    /// <summary>True between Request() and TryConsume() - lets the Map page's normal
    /// first-load auto-center skip itself instead of racing a pending focus request and
    /// briefly centering on the wrong pin first.</summary>
    public static bool HasPending => _latitude.HasValue && _longitude.HasValue;

    public static void Request(string? driverId, double latitude, double longitude)
    {
        _driverId = driverId;
        _latitude = latitude;
        _longitude = longitude;
    }

    public static bool TryConsume(out string? driverId, out double latitude, out double longitude)
    {
        driverId = _driverId;
        latitude = _latitude ?? 0;
        longitude = _longitude ?? 0;
        var hadRequest = _latitude.HasValue && _longitude.HasValue;

        _driverId = null;
        _latitude = null;
        _longitude = null;

        return hadRequest;
    }
}
