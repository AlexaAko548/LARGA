using LARGA.SharedCore;
using LARGA.SharedCore.Services;

namespace LARGA.Tests.Gps;

/// <summary>SRS REQ-4.6-5 / §4.6.2: the Shift Logs route view (ShiftRoute, GeoMath).</summary>
public class ShiftRouteTests
{
    private static readonly DateTime T0 = new(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Haversine_OneDegreeOfLatitude_Is111Km() =>
        Assert.Equal(111.19, GeoMath.HaversineKm(10.0, 123.0, 11.0, 123.0), 2);

    [Fact]
    public void PathDistance_SumsEachLeg()
    {
        var points = new List<(double, double)> { (10.0, 123.0), (10.01, 123.0), (10.01, 123.01) };
        double expected = GeoMath.HaversineKm(10.0, 123.0, 10.01, 123.0) + GeoMath.HaversineKm(10.01, 123.0, 10.01, 123.01);
        Assert.Equal(expected, GeoMath.PathDistanceKm(points), 9);
        Assert.Equal(0, GeoMath.PathDistanceKm(new List<(double, double)> { (10.0, 123.0) }));
    }

    [Fact]
    public void FromPoints_OrdersByTime_DropsNoFix_RoundsDistance()
    {
        var route = ShiftRoute.FromPoints("s1", new[]
        {
            new RoutePoint(10.02, 123.0, 30, T0.AddMinutes(2)),
            new RoutePoint(0, 0, 0, T0.AddMinutes(1)),          // no fix
            new RoutePoint(10.0, 123.0, 0, T0),
            new RoutePoint(10.01, 123.0, 25, T0.AddMinutes(1)),
        });

        Assert.Equal(3, route.Points.Count);
        Assert.Equal(T0, route.StartUtc);
        Assert.Equal(T0.AddMinutes(2), route.EndUtc);
        Assert.Equal(10.0, route.Points[0].Latitude);
        Assert.Equal(2.2, route.DistanceKm);
    }

    [Fact]
    public void FromPoints_NoData_EmptyState()
    {
        var route = ShiftRoute.FromPoints("s1", Array.Empty<RoutePoint>());
        Assert.False(route.HasData);
        Assert.Null(route.StartUtc);
        Assert.Equal(0, route.DistanceKm);
    }
}
