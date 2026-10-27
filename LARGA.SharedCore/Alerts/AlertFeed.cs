using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LARGA.SharedCore.Models.Alerts;
using LARGA.SharedCore.Services;

namespace LARGA.SharedCore.Alerts;

/// <summary>
/// What a manager's alert list is, in one place for both apps: the web's notification bell (AlertService, Admin SDK)
/// and the phone's Alert Center (client SDK). Neither can share the other's Firestore code, so each reads
/// system_alerts with the same query - <see cref="Collection"/> ordered by <see cref="TimestampField"/>, newest first,
/// limited to <see cref="Limit"/> - and hands the rows to the methods here, so the list, the unread count and the
/// day sections come out the same on both.
///
/// ManagerWeb's background services write one system_alerts row per event. SOS, flagged fuel and clock-in requests
/// also have a source record the phone acts on (Resolve / Approve / Deny); <see cref="SourceIdFor"/> maps a row back
/// to it so the phone shows one card, not two.
/// </summary>
public static class AlertFeed
{
    public const string Collection = "system_alerts";
    public const string TimestampField = "timestamp";

    /// <summary>The bell and the Alert Center both show the newest 50 rows.</summary>
    public const int Limit = 50;

    public const string DriverIdleType = "DriverIdle";
    public const string LowStockType = "LowStock";

    /// <summary>Every type a ManagerWeb service writes to system_alerts, with the label both apps show.</summary>
    public static readonly IReadOnlyDictionary<string, string> TypeTitles = new Dictionary<string, string>
    {
        [SosDispatchService.SosAlertType] = "SOS",
        [ClockInApprovalService.PendingAlertType] = "Clock-in approval",
        [FuelVerificationService.FuelAlertType] = "Fuel discrepancy",
        [DriverIdleType] = "Driver idle",
        [LowStockType] = "Low stock",
        [FinancialLedgerService.UnpaidDebtAlertType] = "Unpaid debt",
        [MaintenanceSchedule.DueSoonAlertType] = "Maintenance due soon",
        [MaintenanceSchedule.OverdueAlertType] = "Maintenance overdue",
        [ShiftDeadlineService.LateReturnAlertType] = "Late return",
        [ShiftDeadlineService.MissedClockOutAlertType] = "Missed clock-out",
    };

    public static string TitleFor(string? type) =>
        type is not null && TypeTitles.TryGetValue(type, out string? title) ? title : "Notice";

    /// <summary>The rows to show: newest first, at most <see cref="Limit"/>. The query already does this; doing it
    /// again here keeps the result the same if a caller passes more.</summary>
    public static List<AlertNotice> Recent(IEnumerable<AlertNotice> alerts) =>
        alerts.OrderByDescending(a => a.Timestamp).Take(Limit).ToList();

    /// <summary>The badge number on both apps: unread rows among the newest <see cref="Limit"/>.</summary>
    public static int UnreadCount(IEnumerable<AlertNotice> recent) => recent.Count(a => !a.IsRead);

    // Bell row IDs written alongside a source record (SosDispatchService, FuelVerificationService,
    // ClockInApprovalService).
    public static string SosBellId(string emergencyAlertId) => $"{emergencyAlertId}_SOS";
    public static string FuelBellId(string fuelLogId) => FuelVerificationService.FuelAlertId(fuelLogId);
    public static string ClockInBellId(string requestId) => $"{requestId}_CLOCKIN";

    /// <summary>The source record a bell row stands for (emergency_alerts / fuel_logs / clockin_requests ID), or null
    /// for a row that has none.</summary>
    public static string? SourceIdFor(string bellId, string? type)
    {
        if (type == SosDispatchService.SosAlertType && bellId.EndsWith("_SOS", StringComparison.Ordinal))
            return bellId[..^"_SOS".Length];
        if (type == ClockInApprovalService.PendingAlertType && bellId.EndsWith("_CLOCKIN", StringComparison.Ordinal))
            return bellId[..^"_CLOCKIN".Length];
        if (type == FuelVerificationService.FuelAlertType && bellId.StartsWith("fuel_", StringComparison.Ordinal))
            return bellId["fuel_".Length..];
        return null;
    }

    /// <summary>"Today", "Yesterday" or the date, by the Philippine calendar day - not the phone's or server's
    /// own timezone.</summary>
    public static string SectionTitle(DateTime utc, DateTime philippineNow)
    {
        if (utc == DateTime.MinValue) return "Undated";
        DateTime day = utc.ToPhilippineTime().Date;
        if (day == philippineNow.Date) return "Today";
        if (day == philippineNow.Date.AddDays(-1)) return "Yesterday";
        return day.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>Time only for today, date and time otherwise, in Philippine time.</summary>
    public static string TimeLabel(DateTime utc, DateTime philippineNow)
    {
        if (utc == DateTime.MinValue) return "--";
        DateTime local = utc.ToPhilippineTime();
        return local.Date == philippineNow.Date
            ? local.ToString("h:mm tt", CultureInfo.InvariantCulture)
            : local.ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture);
    }
}
