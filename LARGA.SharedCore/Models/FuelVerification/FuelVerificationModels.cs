using System;
using System.Collections.Generic;
using System.Linq;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore.Models.FuelVerification;

public class FuelLogEntry
{
    public string FuelId { get; set; } = string.Empty;
    public string ShiftId { get; set; } = string.Empty;

    /// <summary>"Refuel #1", "Refuel #2"... - this driver's Nth fuel log on this shift,
    /// ordered by ReceiptTimestamp. Computed, not stored.</summary>
    public string RefuelLabel { get; set; } = string.Empty;

    public string DriverName { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;
    public DateTime? Timestamp { get; set; }

    // LAR-107: the web fuel tabs bind to when the report was actually submitted, not the
    // receipt date above (whatever date the driver's receipt happened to be printed with -
    // a manager can already see that on the receipt image itself inside Review/Resolve).
    public DateTime? SubmittedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public DateTime? FlaggedAt { get; set; }

    /// <summary>Submission time - except on the Flagged tab, which shows when the manager
    /// flagged it. Falls back to the submission (then receipt) time for rows written before
    /// those timestamps existed.</summary>
    public DateTime? StatusTimestamp => Status == FuelVerificationStatus.Flagged
        ? FlaggedAt ?? SubmittedAt ?? Timestamp
        : SubmittedAt ?? Timestamp;

    public decimal Cost { get; set; }
    public decimal Liters { get; set; }
    public int OdometerReading { get; set; }
    public FuelVerificationStatus Status { get; set; }
    public string? FuelStation { get; set; }
    public string? ORNumber { get; set; }
    public string? ReceiptImageUrl { get; set; }
    public string? OdometerPhotoUrl { get; set; }
    public string? FuelLogDetails { get; set; }

    /// <summary>Flagged by the system (non-Petron/Shell station, odometer vs GPS) rather than a
    /// manager - FuelLog.AutoFlagged; the reason is FuelLogDetails.</summary>
    public bool IsAutoFlagged { get; set; }

    /// <summary>The same driver's previous refuel (or, if this is their first, the shift's
    /// starting odometer) - lets the review modal show distance covered and an efficiency
    /// estimate rather than just the raw numbers on their own.</summary>
    public int? PreviousOdometerReading { get; set; }
    public string PreviousOdometerLabel { get; set; } = string.Empty;

    public int? DistanceSinceLastKm =>
        PreviousOdometerReading.HasValue && OdometerReading > PreviousOdometerReading.Value
            ? OdometerReading - PreviousOdometerReading.Value
            : null;

    public decimal? KmPerLiter =>
        DistanceSinceLastKm.HasValue && Liters > 0
            ? Math.Round(DistanceSinceLastKm.Value / Liters, 1)
            : null;
}

public class FuelVerificationSnapshot
{
    public List<FuelLogEntry> Entries { get; set; } = new();

    public int PendingCount => Entries.Count(e => e.Status == FuelVerificationStatus.Pending);
    public int VerifiedCount => Entries.Count(e => e.Status == FuelVerificationStatus.Verified);
    public int FlaggedCount => Entries.Count(e => e.Status == FuelVerificationStatus.Flagged);
}

public class FuelActionResult
{
    public bool Ok { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// The "Review Fuel Submission" modal's full picture: what the driver submitted, next to
/// what the system can independently verify against - GPS-tracked distance for the leg
/// between this refuel and the previous one (or shift start), and a cost/km sanity check.
/// Deliberately not part of FuelLogEntry / the bulk list query - computing GPS distance
/// means summing a shift's telemetry pings, too expensive to do for every row on every
/// page load, so this is fetched on demand only when a manager opens Review on one entry.
/// </summary>
public class FuelReviewDetail
{
    public string FuelId { get; set; } = string.Empty;
    public FuelVerificationStatus Status { get; set; }
    public string DriverName { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;
    public string RefuelLabel { get; set; } = string.Empty;
    public DateTime? Timestamp { get; set; }

    /// <summary>Server time the driver sent the report; null on older reports.</summary>
    public DateTime? SubmittedAt { get; set; }

    public decimal Cost { get; set; }
    public decimal Liters { get; set; }
    public int OdometerReading { get; set; }
    public string? FuelStation { get; set; }
    public string? ORNumber { get; set; }
    public string? ReceiptImageUrl { get; set; }
    public string? OdometerPhotoUrl { get; set; }

    // Read straight from the fuel_logs document (written by the mobile app's OCR scan) - the
    // web page only displays what was extracted, it never runs OCR itself.
    public bool IsCostManuallyEdited { get; set; }
    public bool IsQuantityManuallyEdited { get; set; }
    public bool IsFuelStationManuallyEdited { get; set; }
    public bool IsReceiptDateManuallyEdited { get; set; }
    public bool IsOrNumberManuallyEdited { get; set; }

    public int? PreviousOdometerReading { get; set; }
    public string PreviousOdometerLabel { get; set; } = string.Empty;

    /// <summary>Null if there's no earlier reading to compare against (this driver's very
    /// first refuel on a shift with no recorded starting mileage either).</summary>
    public int? ClaimedDistanceKm =>
        PreviousOdometerReading.HasValue && OdometerReading > PreviousOdometerReading.Value
            ? OdometerReading - PreviousOdometerReading.Value
            : null;

    /// <summary>Cumulative distance from gps_telemetry pings between the comparison window's
    /// start and this refuel's timestamp. Null when telemetry is missing/unavailable for
    /// that window - shown as "no GPS data" rather than treated as a mismatch.</summary>
    public double? GpsDistanceKm { get; set; }

    public double? VarianceKm =>
        ClaimedDistanceKm.HasValue && GpsDistanceKm.HasValue
            ? Math.Abs(ClaimedDistanceKm.Value - GpsDistanceKm.Value)
            : null;

    public double? VariancePercent =>
        VarianceKm.HasValue && GpsDistanceKm is > 0
            ? Math.Round(VarianceKm.Value / GpsDistanceKm.Value * 100, 1)
            : null;

    /// <summary>VariancePercent with its sign: positive when the odometer shows more than GPS.</summary>
    public double? SignedVariancePercent =>
        ClaimedDistanceKm.HasValue && GpsDistanceKm is > 0
            ? Math.Round((ClaimedDistanceKm.Value - GpsDistanceKm.Value) / GpsDistanceKm.Value * 100, 1)
            : null;

    public decimal? CostPerKm =>
        ClaimedDistanceKm is > 0
            ? Math.Round(Cost / ClaimedDistanceKm.Value, 2)
            : null;

    /// <summary>The checks' limits - system_configs/global (FuelTolerances.From), filled in by
    /// FuelVerificationService. Defaults are the original mockup values.</summary>
    public FuelTolerances Tolerances { get; set; } = FuelTolerances.Default;

    /// <summary>This unit's expected km/L (system_configs/global fuelExpectedKmPerLiter), if set.</summary>
    public double? ExpectedKmPerLiter => Tolerances.ExpectedKmPerLiterFor(TaxiId);

    public decimal? KmPerLiter =>
        ClaimedDistanceKm is > 0 && Liters > 0 ? Math.Round(ClaimedDistanceKm.Value / Liters, 1) : null;

    public bool? IsWithinOdometerTolerance =>
        VariancePercent.HasValue ? VariancePercent.Value <= Tolerances.DistanceTolerancePercent : null;

    public bool? IsWithinCostRange =>
        CostPerKm.HasValue ? CostPerKm.Value >= Tolerances.CostPerKmMin && CostPerKm.Value <= Tolerances.CostPerKmMax : null;

    /// <summary>Off by more than the distance tolerance from the unit's expected km/L. Null when no
    /// expected value is set for the unit, or there's no distance/liters to work it out.</summary>
    public bool? IsWithinExpectedKmPerLiter =>
        KmPerLiter is decimal actual && ExpectedKmPerLiter is double expected && expected > 0
            ? Math.Abs((double)actual - expected) / expected * 100 <= Tolerances.DistanceTolerancePercent
            : null;

    /// <summary>Null = "can't tell" (no GPS data to compare against), true = all checks
    /// pass, false = at least one looks off.</summary>
    public bool? MatchesExpectedRange =>
        GpsDistanceKm.HasValue
            ? IsWithinOdometerTolerance == true && IsWithinCostRange != false && IsWithinExpectedKmPerLiter != false
            : null;

    /// <summary>
    /// FUEL-017: why this submission is outside tolerance, one readable line per failed check -
    /// what an automatic flag records as its reason. Empty when everything checks out or when
    /// there's no GPS distance to compare against (that's "can't tell", not a mismatch).
    /// </summary>
    public List<string> ToleranceViolations()
    {
        var reasons = new List<string>();
        if (MatchesExpectedRange is null)
        {
            return reasons;
        }

        if (IsWithinOdometerTolerance == false)
        {
            reasons.Add($"Odometer distance {ClaimedDistanceKm:N0} km vs GPS {GpsDistanceKm:N0} km ({SignedVariancePercent:+0;-0}%)");
        }
        if (IsWithinCostRange == false)
        {
            reasons.Add($"₱{CostPerKm:N2} per km (expected ₱{Tolerances.CostPerKmMin:N2}–₱{Tolerances.CostPerKmMax:N2})");
        }
        if (IsWithinExpectedKmPerLiter == false)
        {
            double off = ((double)KmPerLiter!.Value - ExpectedKmPerLiter!.Value) / ExpectedKmPerLiter.Value * 100;
            reasons.Add($"{KmPerLiter:N1} km/L vs expected {ExpectedKmPerLiter:N1} km/L for {TaxiId} ({off:+0;-0}%)");
        }
        return reasons;
    }
}

/// <summary>
/// FUEL-017 tolerance settings, stored in system_configs/global so they can be tuned without a
/// code change: fuelDistanceTolerancePercent (odometer vs GPS), fuelCostPerKmMin/Max (₱ per km),
/// and fuelExpectedKmPerLiter (optional, per unit). The defaults are the original placeholders.
/// </summary>
public sealed record FuelTolerances(
    double DistanceTolerancePercent,
    decimal CostPerKmMin,
    decimal CostPerKmMax,
    IReadOnlyDictionary<string, double> ExpectedKmPerLiter)
{
    public const double DefaultDistanceTolerancePercent = 8.0;
    public const decimal DefaultCostPerKmMin = 4.50m;
    public const decimal DefaultCostPerKmMax = 7.50m;

    public static FuelTolerances Default { get; } = new(
        DefaultDistanceTolerancePercent, DefaultCostPerKmMin, DefaultCostPerKmMax, new Dictionary<string, double>());

    /// <summary>The config's values, falling back to the defaults for anything unset or out of range.</summary>
    public static FuelTolerances From(SystemConfig? config)
    {
        if (config is null)
        {
            return Default;
        }

        double distance = config.FuelDistanceTolerancePercent > 0 ? config.FuelDistanceTolerancePercent : DefaultDistanceTolerancePercent;
        decimal min = config.FuelCostPerKmMin > 0 ? (decimal)config.FuelCostPerKmMin : DefaultCostPerKmMin;
        decimal max = config.FuelCostPerKmMax > 0 ? (decimal)config.FuelCostPerKmMax : DefaultCostPerKmMax;
        if (min > max)
        {
            (min, max) = (DefaultCostPerKmMin, DefaultCostPerKmMax);
        }

        var perUnit = (config.FuelExpectedKmPerLiter ?? new Dictionary<string, double>())
            .Where(kv => kv.Value > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        return new FuelTolerances(distance, min, max, perUnit);
    }

    public double? ExpectedKmPerLiterFor(string? taxiId) =>
        !string.IsNullOrWhiteSpace(taxiId) && ExpectedKmPerLiter.TryGetValue(taxiId, out double value) ? value : null;
}
