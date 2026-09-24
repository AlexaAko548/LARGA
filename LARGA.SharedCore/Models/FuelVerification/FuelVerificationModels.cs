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
    public decimal Cost { get; set; }
    public decimal Liters { get; set; }
    public int OdometerReading { get; set; }
    public FuelVerificationStatus Status { get; set; }
    public string? FuelStation { get; set; }
    public string? ORNumber { get; set; }
    public string? ReceiptImageUrl { get; set; }
    public string? OdometerPhotoUrl { get; set; }
    public string? FuelLogDetails { get; set; }

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

    public decimal? CostPerKm =>
        ClaimedDistanceKm is > 0
            ? Math.Round(Cost / ClaimedDistanceKm.Value, 2)
            : null;

    // Placeholder tolerance/range values (matching the initial Fuel Verification mockup) -
    // a real fleet-tuned number belongs in system_configs, same as FleetReportingService's
    // idle threshold, once there's real submission data to calibrate against.
    public const double OdometerToleranceComparisonPercent = 8.0;
    public const decimal TypicalCostPerKmMin = 4.50m;
    public const decimal TypicalCostPerKmMax = 7.50m;

    public bool? IsWithinOdometerTolerance =>
        VariancePercent.HasValue ? VariancePercent.Value <= OdometerToleranceComparisonPercent : null;

    public bool? IsWithinCostRange =>
        CostPerKm.HasValue ? CostPerKm.Value >= TypicalCostPerKmMin && CostPerKm.Value <= TypicalCostPerKmMax : null;

    /// <summary>Null = "can't tell" (no GPS data to compare against), true = both checks
    /// pass, false = at least one looks off.</summary>
    public bool? MatchesExpectedRange =>
        GpsDistanceKm.HasValue
            ? IsWithinOdometerTolerance == true && IsWithinCostRange != false
            : null;
}
