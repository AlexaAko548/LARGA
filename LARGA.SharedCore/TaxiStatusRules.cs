using System;

namespace LARGA.SharedCore;

/// <summary>
/// taxis.status values. "Under Maintenance" is the standard; the manager app's "Deny & Send to
/// Garage" used to write "Maintenance", so both are read as under maintenance.
/// </summary>
public static class TaxiStatusRules
{
    public const string UnderMaintenance = "Under Maintenance";

    /// <summary>In service - what a unit goes back to once its last open Garage job is closed.</summary>
    public const string ActiveUnit = "Active Unit";

    /// <summary>maintenance_logs statuses that keep a unit in maintenance: a pending driver
    /// report not yet reviewed, or a work order in the shop.</summary>
    public static bool IsOpenJob(string? maintenanceStatus) =>
        string.Equals(maintenanceStatus, "Reported", StringComparison.OrdinalIgnoreCase)
        || string.Equals(maintenanceStatus, "InProgress", StringComparison.OrdinalIgnoreCase);

    public static bool IsUnderMaintenance(string? status) =>
        string.Equals(status?.Trim(), UnderMaintenance, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status?.Trim(), "Maintenance", StringComparison.OrdinalIgnoreCase);
}
