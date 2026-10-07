using System;
using System.Collections.Generic;
using System.Linq;

namespace LARGA.SharedCore;

/// <summary>
/// Who may start a shift on which unit - one rule for the Schedule Planner, the manager's
/// clock-in approval (web and phone) and the driver's own clock-in, so they never disagree.
///  - No license, no shift (LAR-97): a driver needs a license on file that isn't expired or
///    within one month of expiring.
///  - No shift on a unit under maintenance (LAR-98): the unit isn't marked Under Maintenance
///    and has no Garage work order in the shop that day. A substitute unit is checked the same
///    way, as the unit actually being driven.
/// Reasons are short phrases ("no driver's license is on file") so each screen can word the
/// sentence around them.
/// </summary>
public static class ShiftEligibilityRules
{
    /// <summary>True when the driver can't drive on that Philippine calendar day.</summary>
    public static bool IsLicenseIneligibleOn(DateTime? licenseExpiry, DateTime dayPh) =>
        !licenseExpiry.HasValue || dayPh.Date >= licenseExpiry.Value.AddMonths(-1).Date;

    public static string? LicenseBlockReason(DateTime? licenseExpiry, DateTime dayPh)
    {
        if (!licenseExpiry.HasValue)
        {
            return "no driver's license is on file";
        }

        if (dayPh.Date >= licenseExpiry.Value.Date)
        {
            return $"the driver's license expired on {licenseExpiry.Value:MMM d, yyyy}";
        }

        return IsLicenseIneligibleOn(licenseExpiry, dayPh)
            ? $"the driver's license expires on {licenseExpiry.Value:MMM d, yyyy} (less than a month away)"
            : null;
    }

    /// <summary>
    /// A work order in the shop ("InProgress") covers the days from when it was logged to its
    /// estimated completion - and, while it's still open, every day up to today, since a job
    /// past its estimate is still in the shop until the Garage resolves it. Open-ended without
    /// an estimate. Days are Philippine calendar days; the dates are UTC instants.
    /// </summary>
    public static bool IsJobInShopOn(string? jobStatus, DateTime loggedUtc, DateTime? estimatedCompletionUtc, DateTime dayPh, DateTime todayPh)
    {
        if (!string.Equals(jobStatus, "InProgress", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        DateTime day = dayPh.Date;
        if (day < loggedUtc.ToPhilippineTime().Date)
        {
            return false;
        }

        DateTime? estimate = estimatedCompletionUtc?.ToPhilippineTime().Date;
        return estimate is null || day <= estimate.Value || day <= todayPh.Date;
    }

    /// <summary>Why the unit can't go out today, or null when it can.</summary>
    public static string? UnitBlockReason(
        string taxiId,
        string? taxiStatus,
        IEnumerable<(string? Status, DateTime LoggedUtc, DateTime? EstimatedCompletionUtc, string? Title)> unitJobs,
        DateTime todayPh)
    {
        var inShop = unitJobs.FirstOrDefault(j => IsJobInShopOn(j.Status, j.LoggedUtc, j.EstimatedCompletionUtc, todayPh, todayPh));
        if (inShop.Status is not null)
        {
            string title = string.IsNullOrWhiteSpace(inShop.Title) ? "a Garage work order" : $"\"{inShop.Title.Trim()}\"";
            return $"{taxiId} is in the Garage ({title})";
        }

        return TaxiStatusRules.IsUnderMaintenance(taxiStatus) ? $"{taxiId} is marked Under Maintenance" : null;
    }
}
