namespace LARGA.SharedCore.Emergency;

/// <summary>
/// Where an SOS's coordinates come from (emergency_alerts.locationSource). An SOS is never
/// dropped for lack of GPS: a live fix, else the shift's last telemetry point, else no location
/// at all - 0/0 with source "none", which ManagerWeb and the Alert Center show as "location
/// unavailable".
/// </summary>
public static class SosLocation
{
    public const string SourceLive = "live";
    public const string SourceLastShiftFix = "lastShiftFix";
    public const string SourceNone = "none";

    public readonly record struct Resolved(double Latitude, double Longitude, string Source);

    /// <summary>Picks the alert's coordinates. A 0/0 last shift fix counts as none.</summary>
    public static Resolved Resolve((double Latitude, double Longitude)? live, (double Latitude, double Longitude)? lastShiftFix)
    {
        if (live is (double lat, double lng))
        {
            return new Resolved(lat, lng, SourceLive);
        }
        if (lastShiftFix is (double lastLat, double lastLng) && !(lastLat == 0 && lastLng == 0))
        {
            return new Resolved(lastLat, lastLng, SourceLastShiftFix);
        }
        return new Resolved(0, 0, SourceNone);
    }

    /// <summary>What the dispatch screens treat as "has a location" (0/0 is "unavailable").</summary>
    public static bool HasLocation(double latitude, double longitude) => latitude != 0 || longitude != 0;
}
