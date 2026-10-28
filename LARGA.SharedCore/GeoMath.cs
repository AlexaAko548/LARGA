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

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
}
