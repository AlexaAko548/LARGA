using System.Globalization;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore;
using LARGA.SharedCore.Models.FuelVerification;
using LARGA.SharedCore.Services;

namespace LARGA.Tests;

/// <summary>FUEL-013 and FUEL-016 to FUEL-019 with each row's numbers and the default tolerances
/// (8 % distance, PHP 4.50-7.50 per km, auto-flag from a 10 km leg / 2 km gap).</summary>
public class FuelReviewTests
{
    private static FuelReviewDetail Detail(int previousOdo, int odo, double? gpsKm, decimal cost = 0m, decimal liters = 0m) => new()
    {
        PreviousOdometerReading = previousOdo,
        OdometerReading = odo,
        GpsDistanceKm = gpsKm,
        Cost = cost,
        Liters = liters,
        Tolerances = FuelTolerances.Default,
    };

    // The review modal's own formats (FuelVerification.razor).
    private static string ShownVariance(FuelReviewDetail d) =>
        $"{d.VarianceKm!.Value.ToString("N0", CultureInfo.InvariantCulture)} km ({d.VariancePercent?.ToString("N0", CultureInfo.InvariantCulture)}%)";

    [Fact]
    public void Tolerances_Defaults()
    {
        Assert.Equal(8.0, FuelTolerances.Default.DistanceTolerancePercent);
        Assert.Equal(4.50m, FuelTolerances.Default.CostPerKmMin);
        Assert.Equal(7.50m, FuelTolerances.Default.CostPerKmMax);
        Assert.Equal(10.0, FuelTolerances.Default.MinAutoFlagDistanceKm);
        Assert.Equal(2.0, FuelTolerances.Default.MinAutoFlagVarianceKm);
    }

    // ---------- FUEL-016: 58,600 -> 58,700 (100 km) vs GPS 80 km ----------

    [Fact]
    public void Fuel016_DistanceMismatch_IsOverToleranceAndAutoFlagged()
    {
        var d = Detail(58600, 58700, 80);
        Assert.Equal(100, d.ClaimedDistanceKm);
        Assert.Equal(20, d.VarianceKm);
        Assert.Equal(25.0, d.VariancePercent);
        Assert.Equal("20 km (25%)", ShownVariance(d));
        Assert.False(d.IsWithinOdometerTolerance);
        Assert.False(d.MatchesExpectedRange);
        Assert.Equal("Odometer distance 100 km vs GPS 80 km (+25%)", d.AutoFlagReason());
    }

    // ---------- FUEL-017: 20 L bought, 40 km since the last refuel ----------

    [Fact]
    public void Fuel017_KmPerLiterAndCostPerKm_OutsideRange_IsIndicatorOnly()
    {
        // 20 L at Petron's PHP 56.39/L = PHP 1,127.80; odometer and GPS both 40 km.
        var d = Detail(58600, 58640, 40, cost: 1127.80m, liters: 20m);
        Assert.Equal(2.0m, d.KmPerLiter);
        Assert.Equal(28.20m, d.CostPerKm);
        Assert.False(d.IsWithinCostRange);
        Assert.False(d.MatchesExpectedRange);   // "Review Recommended" badge
        Assert.Null(d.AutoFlagReason());       // per Remarks: indicator only, not auto-flagged
    }

    [Fact]
    public void Fuel017_ReviewModal_ShowsKmPerLiter()
    {
        // The Expected Result says the entry shows km/L. FuelReviewDetail computes it; check the page uses it.
        string razor = File.ReadAllText(FindRepoFile("LARGA.ManagerWeb", "Components", "Pages", "FuelVerification.razor"));
        Assert.Contains("_reviewDetail.KmPerLiter is decimal", razor);
        Assert.Contains("km/L</div>", razor);
    }

    // ---------- FUEL-018: 100 km vs GPS 95 km, PHP 600 ----------

    [Fact]
    public void Fuel018_WithinTolerance_Matches()
    {
        var d = Detail(58600, 58700, 95, cost: 600m);
        Assert.Equal(5, d.VarianceKm);
        Assert.Equal(5.3, d.VariancePercent);         // 5/95, shown rounded as "5%"
        Assert.Equal("5 km (5%)", ShownVariance(d));
        Assert.True(d.IsWithinOdometerTolerance);
        Assert.Equal(6.00m, d.CostPerKm);
        Assert.True(d.IsWithinCostRange);
        Assert.True(d.MatchesExpectedRange);
        Assert.Null(d.AutoFlagReason());
    }

    // ---------- FUEL-019: fewer than 2 telemetry points ----------

    [Fact]
    public void Fuel019_NoGpsData_IsNotAMismatch()
    {
        var d = Detail(58600, 58700, gpsKm: null, cost: 600m);
        Assert.Null(d.VarianceKm);
        Assert.Null(d.VariancePercent);
        Assert.Null(d.IsWithinOdometerTolerance);
        Assert.Null(d.MatchesExpectedRange);    // "No GPS Data To Compare"
        Assert.Null(d.AutoFlagReason());
    }

    [Fact]
    public void Fuel019_Documents_GpsZeroKmWithClaimed100IsNeverAutoFlagged()
    {
        // Two or more pings at one spot sum to 0.0 km (a real reading, not "no data"); VariancePercent
        // divides by GPS and needs > 0, so the comparison is skipped and nothing is flagged.
        var d = Detail(58600, 58700, gpsKm: 0.0);
        Assert.Null(d.VariancePercent);
        Assert.Null(d.IsWithinOdometerTolerance);
        Assert.Null(d.AutoFlagReason());
        Assert.False(d.MatchesExpectedRange);
    }

    // ---------- FUEL-013: receipt dated 2 days before submission ----------

    [Fact]
    public void Fuel013_DateMismatchNote()
    {
        var receipt = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);   // receiptTimestamp: midnight UTC
        var submitted = new DateTime(2026, 10, 24, 4, 46, 0, DateTimeKind.Utc); // 12:46 PM Philippine time
        Assert.Equal("Receipt dated Oct 22, 2026, submitted 2 days later (Oct 24, 2026).",
            FuelReportDates.MismatchNote(receipt, submitted));
    }

    [Fact]
    public void Fuel013_EmulatorShellSubmission_Note()
    {
        // fuel_logs/tK3AJrMmJUHhCo71PO5X as written on the emulator run.
        Assert.Equal("Receipt dated Oct 8, 2026, submitted 16 days later (Oct 24, 2026).",
            FuelReportDates.MismatchNote(new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 24, 4, 46, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Fuel013_SameDay_NoNote() =>
        Assert.Null(FuelReportDates.MismatchNote(new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 24, 4, 0, 0, DateTimeKind.Utc)));

    // ---------- FUEL-020 / auto-check bookkeeping ----------

    [Fact]
    public void Fuel020_NeedsAutoCheck()
    {
        Assert.True(FuelVerificationService.NeedsAutoCheck(new FuelLog { VerificationStatus = FuelVerificationStatus.Pending }));
        Assert.False(FuelVerificationService.NeedsAutoCheck(new FuelLog { VerificationStatus = FuelVerificationStatus.Pending, AutoCheckedAt = DateTime.UtcNow }));
        Assert.True(FuelVerificationService.NeedsAutoCheck(new FuelLog { VerificationStatus = FuelVerificationStatus.Flagged, AutoFlagged = true }));
        Assert.False(FuelVerificationService.NeedsAutoCheck(new FuelLog { VerificationStatus = FuelVerificationStatus.Flagged }));
    }

    internal static string FindRepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }
}
