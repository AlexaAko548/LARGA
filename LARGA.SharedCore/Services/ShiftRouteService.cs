using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore.Services;

/// <summary>One GPS ping on a shift's route (gps_telemetry), UTC.</summary>
public record RoutePoint(double Latitude, double Longitude, int Speed, DateTime TimestampUtc);

/// <summary>A shift's GPS route for the Shift Logs "View route" map (SRS REQ-4.6-5, §4.6.2).</summary>
public class ShiftRoute
{
    public string ShiftId { get; init; } = string.Empty;

    /// <summary>In time order. Empty when the shift has no GPS data - never tracked, or its
    /// telemetry already expired (expireAt, 90 days).</summary>
    public IReadOnlyList<RoutePoint> Points { get; init; } = Array.Empty<RoutePoint>();

    public bool HasData => Points.Count > 0;

    /// <summary>Total distance along the points, km (one decimal).</summary>
    public double DistanceKm { get; init; }

    public DateTime? StartUtc => HasData ? Points[0].TimestampUtc : null;
    public DateTime? EndUtc => HasData ? Points[^1].TimestampUtc : null;

    /// <summary>Points in order, with pings at 0,0 (no fix) left out, and the distance along them.</summary>
    public static ShiftRoute FromPoints(string shiftId, IEnumerable<RoutePoint> points)
    {
        List<RoutePoint> ordered = points
            .Where(p => !(p.Latitude == 0 && p.Longitude == 0)
                        && p.Latitude is >= -90 and <= 90 && p.Longitude is >= -180 and <= 180)
            .OrderBy(p => p.TimestampUtc)
            .ToList();

        return new ShiftRoute
        {
            ShiftId = shiftId,
            Points = ordered,
            DistanceKm = Math.Round(GeoMath.PathDistanceKm(ordered.Select(p => (p.Latitude, p.Longitude)).ToList()), 1),
        };
    }
}

/// <summary>Reads a shift's gps_telemetry for the route map (index shiftId + timestamp, the same
/// one the fuel review's GPS distance uses).</summary>
public class ShiftRouteService
{
    private readonly Lazy<FirestoreDb> _db;

    public ShiftRouteService(Lazy<FirestoreDb> db)
    {
        _db = db;
    }

    public async Task<ShiftRoute> GetShiftRouteAsync(string shiftId)
    {
        QuerySnapshot snapshot = await _db.Value.Collection("gps_telemetry")
            .WhereEqualTo("shiftId", shiftId)
            .OrderBy("timestamp")
            .GetSnapshotAsync();

        IEnumerable<RoutePoint> points = snapshot.Documents
            .Select(d => d.ConvertTo<GpsTelemetry>())
            .Select(g => new RoutePoint(g.Latitude, g.Longitude, g.Speed, DateTime.SpecifyKind(g.Timestamp, DateTimeKind.Utc)));
        return ShiftRoute.FromPoints(shiftId, points);
    }
}
