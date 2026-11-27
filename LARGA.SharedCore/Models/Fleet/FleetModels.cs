using System.Collections.Generic;

namespace LARGA.SharedCore.Models.Fleet;

/// <summary>One row of ManagerWeb's Fleet Units table.</summary>
public class FleetUnitEntry
{
    public string TaxiId { get; set; } = string.Empty;
    public string PlateNumber { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int YearManufactured { get; set; }

    /// <summary>The figure on the unit's record - what the Edit form changes.</summary>
    public int RecordedMileage { get; set; }

    /// <summary>The higher of the recorded figure and the latest shift's odometer reading in the last
    /// 30 days - an odometer only goes up, so the higher one is the newer.</summary>
    public int CurrentMileage { get; set; }

    public string Status { get; set; } = string.Empty;
    public bool IsRetired { get; set; }
    public string? AssignedDriverName { get; set; }
}

/// <summary>What the Add / Edit Unit form submits.</summary>
public class FleetUnitInput
{
    public string PlateNumber { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int? YearManufactured { get; set; }
    public int? Mileage { get; set; }

    /// <summary>Edit only: "Active Unit" or "Under Maintenance". Null leaves the status alone.</summary>
    public string? Status { get; set; }
}

public class FleetActionResult
{
    public bool Ok { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>The unit acted on (the new ID after an add).</summary>
    public string? TaxiId { get; set; }

    /// <summary>Delete only: true when the unit was retired because it has history, false when its
    /// record was permanently removed.</summary>
    public bool Retired { get; set; }

    public static FleetActionResult Success(string taxiId, bool retired = false) => new() { Ok = true, TaxiId = taxiId, Retired = retired };
    public static FleetActionResult Fail(string message) => new() { Ok = false, ErrorMessage = message };
}

/// <summary>What Delete would do to a unit, for the confirmation shown first.</summary>
public class UnitRemovalPlan
{
    /// <summary>Non-empty means it can't be deleted yet.</summary>
    public List<string> Blockers { get; set; } = new();

    /// <summary>True when the unit has records, so it will be retired rather than removed.</summary>
    public bool HasHistory { get; set; }
}

/// <summary>What deleting a driver would run into, for the confirmation shown first.</summary>
public class DriverRemovalPlan
{
    public string FullName { get; set; } = string.Empty;
    public List<string> Blockers { get; set; } = new();
    public decimal AdvanceCredit { get; set; }
    public string? AssignedTaxiId { get; set; }
}

public class DriverRemovalResult
{
    public bool Ok { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>The driver was removed, but a clean-up step failed.</summary>
    public string? Warning { get; set; }

    public static DriverRemovalResult Fail(string message) => new() { Ok = false, ErrorMessage = message };
}
