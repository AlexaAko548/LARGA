using System;

namespace LARGA.SharedCore;

/// <summary>
/// Garage work-order lifecycle (maintenance_logs.status):
///   Reported   - a driver's defect report, not yet reviewed;
///   Scheduled  - a ticket booked for a later shop day (scheduledDate);
///   InProgress - in the shop now;
///   Resolved / Dismissed - closed.
/// A ticket booked for today goes straight to InProgress. A Scheduled ticket becomes InProgress
/// on its day (the Garage page moves it), but every check treats it as in the shop from that
/// day already, so nothing depends on someone opening the Garage first.
/// </summary>
public static class WorkOrderRules
{
    public const string Reported = "Reported";
    public const string Scheduled = "Scheduled";
    public const string InProgress = "InProgress";

    /// <summary>The day the unit goes into the shop: its scheduled date or, for a ticket that
    /// started right away (and older ones made before scheduling existed), when it was logged.</summary>
    public static DateTime ShopStartUtc(DateTime loggedUtc, DateTime? scheduledUtc) => scheduledUtc ?? loggedUtc;

    /// <summary>A status that puts the unit in the shop from its start day on.</summary>
    public static bool IsShopStatus(string? status) =>
        string.Equals(status, InProgress, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, Scheduled, StringComparison.OrdinalIgnoreCase);

    /// <summary>In the shop right now: in progress, or scheduled for today or earlier.</summary>
    public static bool IsInShopNow(string? status, DateTime loggedUtc, DateTime? scheduledUtc, DateTime todayPh) =>
        IsShopStatus(status) && ShopStartUtc(loggedUtc, scheduledUtc).ToPhilippineTime().Date <= todayPh.Date;

    /// <summary>Day 1 = the first day in the shop.</summary>
    public static int ShopDayNumber(DateTime loggedUtc, DateTime? scheduledUtc, DateTime todayPh) =>
        Math.Max(1, (int)(todayPh.Date - ShopStartUtc(loggedUtc, scheduledUtc).ToPhilippineTime().Date).TotalDays + 1);
}
