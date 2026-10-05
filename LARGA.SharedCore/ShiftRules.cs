using System;

namespace LARGA.SharedCore;

/// <summary>
/// BLM Taxi's operating-day rules, from the capstone paper (Ch. IV BPMN 4.2.4.1 / 4.2.4.4 and
/// the owner interview): units go out from 6:00 AM and are due back by 10:00 PM, Philippine
/// time. Returning late is allowed but charged per hour; the unit coming back is what's timed
/// (clock-out), not the driver paying. Shared by the mobile app (clock-in gate, countdown) and
/// ManagerWeb (ledger late fee, background auto-close) so both sides compute the same thing.
///
/// All inputs/outputs are UTC; the rules themselves are evaluated on the Philippine calendar
/// day the shift started on (see PhilippineTime).
/// </summary>
public static class ShiftRules
{
    /// <summary>Earliest clock-in, Philippine time.</summary>
    public static readonly TimeSpan ClockInOpensAt = TimeSpan.FromHours(6);

    /// <summary>Unit due back, Philippine time.</summary>
    public static readonly TimeSpan ReturnBy = TimeSpan.FromHours(22);

    /// <summary>Returns up to this many minutes past ReturnBy aren't charged (10:00–10:29 PM).</summary>
    public const int LateGraceMinutes = 30;

    public const decimal LateFeePerHour = 100m;

    /// <summary>The boundary (daily rent) used when system_configs/global has no positive
    /// defaultBoundaryRate. The one fallback every ledger (web and phone) uses.</summary>
    public const decimal DefaultBoundaryRate = 800m;

    /// <summary>Returning the unit with the fuel below half-tank (paper Fig. 10: end-of-shift
    /// fuel check). Charged on the shift being ended, on top of the boundary.</summary>
    public const decimal LowFuelPenalty = 50m;

    /// <summary>A shift still open at the next operating day's opening (6:00 AM the day after it
    /// started) can't be a legitimate late return any more - it's a missed clock-out.</summary>
    public static DateTime AutoCloseAtUtc(DateTime shiftStartUtc) =>
        PhDayStartUtc(shiftStartUtc).AddDays(1) + ClockInOpensAt;

    public static DateTime ReturnDeadlineUtc(DateTime shiftStartUtc) =>
        PhDayStartUtc(shiftStartUtc) + ReturnBy;

    /// <summary>Clock-in is only possible from 6:00 AM (Philippine time).</summary>
    public static bool CanClockIn(DateTime utcNow) =>
        utcNow.ToPhilippineTime().TimeOfDay >= ClockInOpensAt;

    /// <summary>
    /// Late-return fee for a shift ended at <paramref name="shiftEndUtc"/>:
    /// up to 10:29 PM ₱0 (unwritten grace), 10:30–11:00 PM ₱100, 11:01 PM–12:00 AM ₱200,
    /// and another ₱100 for every further started hour. Measured in whole minutes, so
    /// 11:00:45 PM still counts as 11:00.
    /// </summary>
    public static decimal LateReturnFee(DateTime shiftStartUtc, DateTime shiftEndUtc)
    {
        int minutesLate = (int)Math.Floor((shiftEndUtc - ReturnDeadlineUtc(shiftStartUtc)).TotalMinutes);
        if (minutesLate < LateGraceMinutes)
        {
            return 0m;
        }

        return LateFeePerHour * (int)Math.Ceiling(minutesLate / 60.0);
    }

    /// <summary>Paper Ch. IV (end of shift): a driver who "has not settled their outstanding
    /// balance for three (3) or more consecutive days" gets their account flagged and the
    /// manager notified.</summary>
    public const int DebtFlagDays = 3;

    /// <summary>Whole Philippine calendar days a balance from a shift that started at
    /// <paramref name="oldestUnpaidShiftStartUtc"/> has gone unpaid - a Monday shortfall is
    /// 3 days unpaid on Thursday.</summary>
    public static int DaysUnpaid(DateTime oldestUnpaidShiftStartUtc, DateTime nowUtc) =>
        Math.Max(0, (nowUtc.ToPhilippineTime().Date - oldestUnpaidShiftStartUtc.ToPhilippineTime().Date).Days);

    public static bool IsDebtOverdue(DateTime oldestUnpaidShiftStartUtc, DateTime nowUtc) =>
        DaysUnpaid(oldestUnpaidShiftStartUtc, nowUtc) >= DebtFlagDays;

    /// <summary>The garage / drop-off site where units must be returned (BLM Taxi). The
    /// system_configs/global fields garageLatitude/garageLongitude/garageRadiusMeters
    /// override these when set.</summary>
    public const double GarageLatitude = 10.25328600512787;
    public const double GarageLongitude = 123.86146366850299;

    /// <summary>How close to the garage pin the driver must be to end a shift.</summary>
    public const double GarageRadiusMeters = 150;

    /// <summary>Great-circle distance in meters between two coordinates (haversine).</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusMeters = 6_371_000;
        double dLat = ToRadians(lat2 - lat1);
        double dLon = ToRadians(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

    /// <summary>UTC instant of midnight, Philippine time, on the PH calendar day of <paramref name="utc"/>.</summary>
    private static DateTime PhDayStartUtc(DateTime utc) =>
        DateTime.SpecifyKind(utc.ToPhilippineTime().Date - PhilippineTime.Offset, DateTimeKind.Utc);
}
