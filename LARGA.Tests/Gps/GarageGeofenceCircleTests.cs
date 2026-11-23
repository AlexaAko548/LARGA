using LARGA.SharedCore;

namespace LARGA.Tests.Gps;

/// <summary>The garage geofence circle on the manager map (GeoMath.CirclePoints).</summary>
public class GarageGeofenceCircleTests
{
    [Fact]
    public void EveryPoint_IsOnTheRadius()
    {
        var points = GeoMath.CirclePoints(ShiftRules.GarageLatitude, ShiftRules.GarageLongitude, ShiftRules.GarageRadiusMeters);

        Assert.Equal(64, points.Count);
        foreach (var (lat, lon) in points)
        {
            double meters = GeoMath.HaversineKm(ShiftRules.GarageLatitude, ShiftRules.GarageLongitude, lat, lon) * 1000;
            Assert.InRange(meters, ShiftRules.GarageRadiusMeters - 0.5, ShiftRules.GarageRadiusMeters + 0.5);
        }
    }

    [Fact]
    public void FirstPoint_IsDueNorth()
    {
        var (lat, lon) = GeoMath.CirclePoints(10.0, 123.0, 1000, segments: 8)[0];
        Assert.True(lat > 10.0);
        Assert.Equal(123.0, lon, 9);
    }
}
