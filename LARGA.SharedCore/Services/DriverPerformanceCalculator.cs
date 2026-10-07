using System;
using System.Collections.Generic;
using System.Linq;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore.Services;

/// <summary>A driver's performance over a period - the Driver &amp; Shifts profile and the
/// Executive Dashboard's Top Driver Standings both show these.</summary>
public class DriverPerformance
{
    public DateTime FromPh { get; set; }
    public DateTime ToPh { get; set; }

    /// <summary>Days they were expected to drive (see DriverPerformanceCalculator).</summary>
    public int ExpectedDays { get; set; }
    public int DaysWorked { get; set; }
    public int MissedDays { get; set; }

    /// <summary>Shifts that have ended.</summary>
    public int ShiftsCompleted { get; set; }
    public int LateReturns { get; set; }

    /// <summary>Ended shifts whose boundary is due by now.</summary>
    public int BoundariesDue { get; set; }
    public int BoundariesPaidOnTime { get; set; }

    /// <summary>Accident / damage reports from their shifts.</summary>
    public int DamageIncidents { get; set; }

    /// <summary>SOS alerts they raised in the period (resolved or not).</summary>
    public int SosAlerts { get; set; }

    /// <summary>Everything shown as "Incidents": SOS alerts plus accident / damage reports.
    /// Informational only - it isn't part of the ranking score, since an SOS (a robbery, a
    /// medical emergency) isn't necessarily the driver's fault.</summary>
    public int Incidents => SosAlerts + DamageIncidents;
    public decimal BoundariesRemitted { get; set; }

    /// <summary>Worked days / expected days; null when no day was expected.</summary>
    public double? AttendancePercent => ExpectedDays == 0 ? null : 100.0 * Math.Min(DaysWorked, ExpectedDays) / ExpectedDays;

    /// <summary>On time = showed up on an expected day AND returned the unit by the deadline.
    /// A missed day or a late return counts against it.</summary>
    public double? PunctualityPercent =>
        ShiftsCompleted + MissedDays == 0 ? null
        : 100.0 * (ShiftsCompleted - LateReturns) / (ShiftsCompleted + MissedDays);

    /// <summary>Boundaries paid in full by the end of the day after the shift.</summary>
    public double? PaymentReliabilityPercent => BoundariesDue == 0 ? null : 100.0 * BoundariesPaidOnTime / BoundariesDue;

    /// <summary>The leaderboard's ranking score: the average of the three percentages a driver
    /// has (a driver with none scores -1, after everyone with a record).</summary>
    public double Score
    {
        get
        {
            double?[] parts = { AttendancePercent, PunctualityPercent, PaymentReliabilityPercent };
            List<double> known = parts.OfType<double>().ToList();
            return known.Count == 0 ? -1 : known.Average();
        }
    }
}

/// <summary>
/// Works out a driver's performance from their records, by the rules the rest of the system
/// uses:
///  - Expected days (the Schedule Planner's default): every day the driver has an assigned
///    unit, from the day they joined - except a Day Off, a day their license wasn't valid, or a
///    day their own unit was in the Garage (a work order covering that day). A day they did
///    drive always counts as expected, even on a substitute unit.
///  - Punctuality: a shift is on time when its unit came back by 10:29 PM (ShiftRules - no late
///    fee) and it wasn't auto-closed for a missed clock-out. Every expected day they didn't
///    show up for counts as not punctual.
///  - Payment reliability: the shift's boundary (plus its extra charges) was paid in full by
///    the end of the next day, counting every payment document (BoundaryPaymentRules).
///  - Incidents: SOS alerts the driver raised in the period, plus accident / damage reports
///    from their shifts. Routine defect reports aren't incidents. Incidents are shown, not
///    scored.
/// Today only counts once the driver has driven today - the day isn't over.
/// </summary>
public static class DriverPerformanceCalculator
{
    public static DriverPerformance Calculate(
        UserProfile driver,
        IReadOnlyList<ShiftLog> driverShifts,
        IReadOnlyList<ShiftSchedule> driverExceptions,
        IReadOnlyList<MaintenanceRecord> maintenance,
        IReadOnlyList<BoundaryPayment> payments,
        IReadOnlyList<EmergencyAlert> alerts,
        decimal defaultBoundaryRate,
        DateTime fromPh,
        DateTime nowUtc)
    {
        DateTime todayPh = nowUtc.ToPhilippineTime().Date;
        DateTime joinedPh = driver.DateJoined?.ToPhilippineTime().Date ?? DateTime.MinValue;
        DateTime firstShiftPh = driverShifts
            .Select(s => s.ShiftStart?.ToPhilippineTime().Date)
            .OfType<DateTime>()
            .DefaultIfEmpty(DateTime.MaxValue)
            .Min();
        // A driver without a join date counts from their first shift.
        DateTime startPh = new[] { fromPh.Date, driver.DateJoined is null ? firstShiftPh : joinedPh }.Max();

        var result = new DriverPerformance { FromPh = startPh, ToPh = todayPh };

        List<ShiftLog> shifts = driverShifts
            .Where(s => s.ShiftStart is DateTime start && start.ToPhilippineTime().Date >= startPh && start <= nowUtc)
            .ToList();
        HashSet<DateTime> workedDays = shifts.Select(s => s.ShiftStart!.Value.ToPhilippineTime().Date).ToHashSet();

        // --- Attendance ----------------------------------------------------------------------
        string? ownUnit = string.IsNullOrWhiteSpace(driver.AssignedTaxiId) ? null : driver.AssignedTaxiId;
        for (DateTime day = startPh; day <= todayPh; day = day.AddDays(1))
        {
            bool worked = workedDays.Contains(day);
            if (day == todayPh && !worked)
            {
                continue; // today isn't over yet
            }

            bool expected = worked || (ownUnit is not null
                && !IsDayOff(driverExceptions, day)
                && !IsLicenseInvalidOn(driver.LicenseExpiryDate, day)
                && !IsUnitInShop(maintenance, ownUnit, day, todayPh));
            if (!expected)
            {
                continue;
            }

            result.ExpectedDays++;
            if (worked) result.DaysWorked++;
            else result.MissedDays++;
        }

        // --- On-time returns -----------------------------------------------------------------
        foreach (ShiftLog shift in shifts.Where(s => s.ShiftEnd is not null || IsAutoClosed(s)))
        {
            result.ShiftsCompleted++;
            bool late = IsAutoClosed(shift)
                || (shift.ShiftEnd is DateTime end && ShiftRules.LateReturnFee(shift.ShiftStart!.Value, end) > 0)
                || shift.LateFee is > 0;
            if (late) result.LateReturns++;
        }

        // --- Payments --------------------------------------------------------------------------
        foreach (ShiftLog shift in shifts.Where(s => s.ShiftEnd is not null || IsAutoClosed(s)))
        {
            List<string> ids = new[] { shift.DocumentId, shift.ShiftId }.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
            List<BoundaryPayment> docs = payments.Where(p => ids.Contains(p.ShiftId)).ToList();
            BoundaryPayment? latest = docs.OrderByDescending(d => d.RecordedAt).FirstOrDefault();

            decimal boundary = latest is not null && latest.ExpectedBoundary > 0 ? latest.ExpectedBoundary : defaultBoundaryRate;
            decimal extras = (decimal)(shift.LateFee ?? 0) + (decimal)(shift.FuelPenalty ?? 0);
            decimal expectedAmount = boundary + extras;
            decimal paid = BoundaryPaymentRules.TotalPaid(docs.Select(d => (d.PaymentId, d.TransactionId, d.AmountPaid)));
            result.BoundariesRemitted += paid;

            // Due by the end of the day after the shift (Philippine time).
            DateTime dueByUtc = shift.ShiftStart!.Value.ToPhilippineTime().Date.AddDays(2) - PhilippineTime.Offset;
            if (dueByUtc > nowUtc)
            {
                continue; // not due yet
            }

            result.BoundariesDue++;
            decimal paidByDue = BoundaryPaymentRules.TotalPaid(docs
                .Where(d => d.RecordedAt <= dueByUtc)
                .Select(d => (d.PaymentId, d.TransactionId, d.AmountPaid)));
            if (paidByDue >= expectedAmount || latest?.PaymentStatus == PaymentStatus.Paid && latest.RecordedAt <= dueByUtc)
            {
                result.BoundariesPaidOnTime++;
            }
        }

        // --- Damage incidents ------------------------------------------------------------------
        HashSet<string> shiftIds = shifts.SelectMany(s => new[] { s.DocumentId, s.ShiftId }).Where(id => !string.IsNullOrEmpty(id)).ToHashSet();
        result.DamageIncidents = maintenance.Count(m => m.MaintenanceType == MaintenanceType.AccidentCorrection
            && m.ShiftId is not null && shiftIds.Contains(m.ShiftId)
            && !string.Equals(m.Status, "Dismissed", StringComparison.OrdinalIgnoreCase));

        // SOS alerts: matched by driver, or by one of their shifts (older alerts may lack driverId).
        DateTime startUtc = startPh - PhilippineTime.Offset;
        result.SosAlerts = alerts.Count(a => a.Timestamp >= startUtc && a.Timestamp <= nowUtc
            && ((!string.IsNullOrEmpty(a.DriverId) && a.DriverId == driver.UserId)
                || (!string.IsNullOrEmpty(a.ShiftId) && shiftIds.Contains(a.ShiftId))));

        return result;
    }

    private static bool IsAutoClosed(ShiftLog shift) =>
        string.Equals(shift.Status, ShiftManagementService.AutoClosedStatus, StringComparison.OrdinalIgnoreCase);

    private static bool IsDayOff(IReadOnlyList<ShiftSchedule> exceptions, DateTime dayPh) =>
        exceptions.Any(e => e.Status == "DayOff" && e.ScheduledStartTime.Date == dayPh);

    // Same as the Schedule Planner (DriverManagementService.IsLicenseIneligibleOn): no expiry
    // on file, or expired by that day.
    private static bool IsLicenseInvalidOn(DateTime? expiry, DateTime dayPh) =>
        expiry is null || expiry.Value.Date < dayPh;

    /// <summary>A Garage job on the unit covered that day: from its shop day (WorkOrderRules.ShopStartUtc)
    /// until it was resolved (or, if still open, its estimated finish - open-ended without one).</summary>
    private static bool IsUnitInShop(IReadOnlyList<MaintenanceRecord> maintenance, string taxiId, DateTime dayPh, DateTime todayPh) =>
        maintenance.Any(m =>
            string.Equals(m.TaxiId, taxiId, StringComparison.OrdinalIgnoreCase)
            && (WorkOrderRules.IsShopStatus(m.Status)
                || string.Equals(m.Status, "Resolved", StringComparison.OrdinalIgnoreCase))
            && dayPh >= WorkOrderRules.ShopStartUtc(m.DateLogged, m.ScheduledDate).ToPhilippineTime().Date
            && dayPh <= (m.DateResolved?.ToPhilippineTime().Date
                ?? m.EstimatedCompletionDate?.ToPhilippineTime().Date
                ?? todayPh));
}
