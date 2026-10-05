using System;

namespace LARGA.SharedCore;

/// <summary>
/// taxis.status values. "Under Maintenance" is the standard; the manager app's "Deny & Send to
/// Garage" used to write "Maintenance", so both are read as under maintenance.
/// </summary>
public static class TaxiStatusRules
{
    public const string UnderMaintenance = "Under Maintenance";

    public static bool IsUnderMaintenance(string? status) =>
        string.Equals(status?.Trim(), UnderMaintenance, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status?.Trim(), "Maintenance", StringComparison.OrdinalIgnoreCase);
}
