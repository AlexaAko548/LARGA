using System.Collections.Generic;

namespace LARGA.SharedCore.Models.Dashboard;

/// <summary>
/// Everything the ManagerWeb Executive Dashboard needs to render for one page load.
/// Built by <see cref="Services.FleetReportingService"/> - not a Firestore document itself.
/// </summary>
public class DashboardSnapshot
{
    public StatCard WeeklyRevenue { get; set; } = new();
    public ActiveDriversStat ActiveDrivers { get; set; } = new();
    public StatCard FleetMileage { get; set; } = new();
    public StatCard OpenIncidents { get; set; } = new();

    public FleetStatusCounts FleetStatus { get; set; } = new();

    public List<DriverStanding> TopDrivers { get; set; } = new();

    /// <summary>Paid vs. Outstanding boundary amounts, most recent week first.</summary>
    public List<ChartPoint> BoundaryCollections { get; set; } = new();

    /// <summary>Fuel log count grouped by VerificationStatus (Verified/Pending/Flagged).</summary>
    public List<ChartPoint> FuelVerification { get; set; } = new();

    /// <summary>Distance covered per taxi, most recent week.</summary>
    public List<ChartPoint> FleetMileageByTaxi { get; set; } = new();

    /// <summary>Maintenance TotalCost grouped by MaintenanceType.</summary>
    public List<ChartPoint> MaintenanceExpenses { get; set; } = new();
}

/// <summary>A single stat-card value with a trend badge (e.g. "+12%" or "-1").</summary>
public class StatCard
{
    public decimal Value { get; set; }
    public double? TrendPercent { get; set; }
    public decimal? TrendAbsolute { get; set; }
}

/// <summary>The "Active Drivers" card is a ratio (n / total), not a plain value + trend.</summary>
public class ActiveDriversStat
{
    public int ActiveCount { get; set; }
    public int TotalDrivers { get; set; }
    public double RatioPercent => TotalDrivers == 0 ? 0 : 100.0 * ActiveCount / TotalDrivers;
}

/// <summary>Live, computed per-taxi status counts - see docs/ERD.md "Live fleet status".</summary>
public class FleetStatusCounts
{
    public int Active { get; set; }
    public int Maintenance { get; set; }
    public int OnBreak { get; set; }
    public int Sos { get; set; }
    public int Idle { get; set; }
}

public class DriverStanding
{
    public int Rank { get; set; }
    public string DriverId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;
    public double PunctualPercent { get; set; }
    public int IncidentCount { get; set; }
    public decimal BoundariesRemitted { get; set; }
}

/// <summary>Generic (label, value) pair for chart series.</summary>
public class ChartPoint
{
    public string Label { get; set; } = string.Empty;
    public decimal Value { get; set; }
}
