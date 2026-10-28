using System;
using System.Collections.Generic;

namespace LARGA.SharedCore;

/// <summary>Great-circle distances for GPS points (gps_telemetry). Shared by the fuel review's
/// odometer-vs-GPS check (FuelVerificationService) and the shift route view (ShiftRouteService).</summary>
public static class GeoMath
{
    private const double EarthRadiusKm = 6371.0;

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = DegreesToRadians(lat2 - lat1);
        double dLon = DegreesToRadians(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                   + Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2))
                   * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusKm * c;
    }

    /// <summary>The length of the path through the points in order, in km (0 for fewer than two).</summary>
    public static double PathDistanceKm(IReadOnlyList<(double Latitude, double Longitude)> points)
    {
        double totalKm = 0;
        for (int i = 1; i < points.Count; i++)
        {
            totalKm += HaversineKm(points[i - 1].Latitude, points[i - 1].Longitude, points[i].Latitude, points[i].Longitude);
        }
        return totalKm;
    }

    /// <summary>
    /// <paramref name="segments"/> points (latitude, longitude) on the circle of
    /// <paramref name="radiusMeters"/> around a center - the outline of a geofence such as the garage
    /// (ShiftRules.GarageRadiusMeters), drawn on the manager map. The first point is due north and the
    /// ring isn't closed (the map closes it).
    /// </summary>
    public static List<(double Latitude, double Longitude)> CirclePoints(double latitude, double longitude, double radiusMeters, int segments = 64)
    {
        double angular = radiusMeters / 1000.0 / EarthRadiusKm;
        double lat1 = DegreesToRadians(latitude);
        double lon1 = DegreesToRadians(longitude);
        var points = new List<(double, double)>(segments);
        for (int i = 0; i < segments; i++)
        {
            double bearing = 2 * Math.PI * i / segments;
            double lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(angular) + Math.Cos(lat1) * Math.Sin(angular) * Math.Cos(bearing));
            double lon2 = lon1 + Math.Atan2(Math.Sin(bearing) * Math.Sin(angular) * Math.Cos(lat1),
                                            Math.Cos(angular) - Math.Sin(lat1) * Math.Sin(lat2));
            points.Add((lat2 * 180.0 / Math.PI, lon2 * 180.0 / Math.PI));
        }
        return points;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
}
