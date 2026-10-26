using System;
using System.Collections.Generic;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore;

/// <summary>
/// MNT-014/015/016, UAT-042: the recurring preventive-maintenance checks every active unit gets,
/// and how often (system_configs/global, with these defaults). ManagerWeb's
/// MaintenanceSchedulerService keeps the next one of each in routine_checks
/// (GarageService.EnsureRecurringChecksAsync) and raises DueSoon/Overdue bell alerts.
/// </summary>
public static class MaintenanceSchedule
{
    public const string WeeklyInspection = "WeeklyInspection";
    public const string BiweeklyCheckup = "BiweeklyCheckup";
    public const string MonthlyInspection = "MonthlyInspection";
    public const string OilChange = "OilChange";

    public const int DefaultWeeklyIntervalDays = 7;
    public const int DefaultBiweeklyIntervalDays = 14;
    public const int DefaultMonthlyIntervalDays = 30;
    public const int DefaultOilChangeIntervalDays = 75; // every 2.5 months
    public const int DefaultDueSoonDays = 2;

    public const string DueSoonAlertType = "MaintenanceDueSoon";
    public const string OverdueAlertType = "MaintenanceOverdue";

    public static readonly IReadOnlyList<string> RecurringTypes = new[] { WeeklyInspection, BiweeklyCheckup, MonthlyInspection, OilChange };

    /// <summary>The check name shown on the Garage page and in alerts.</summary>
    public static string DisplayName(string type) => type switch
    {
        WeeklyInspection => "Weekly inspection",
        BiweeklyCheckup => "Bi-weekly checkup",
        MonthlyInspection => "Monthly comprehensive inspection",
        OilChange => "Oil change",
        _ => type,
    };

    /// <summary>Which recurring type a check or work order is: its routineType when it has one,
    /// else read from its name - so hand-entered checks ("Oil Change") and work orders booked
    /// before routineType existed count too, and aren't duplicated. Null for anything else
    /// (e.g. "Brake Pad Replacement").</summary>
    public static string? TypeOf(string? routineType, string? name)
    {
        if (!string.IsNullOrWhiteSpace(routineType))
        {
            return routineType;
        }

        string n = (name ?? string.Empty).ToLowerInvariant();
        if (n.Contains("oil change")) return OilChange;
        if (n.Contains("bi-weekly") || n.Contains("biweekly") || n.Contains("bi weekly")) return BiweeklyCheckup;
        if (n.Contains("weekly")) return WeeklyInspection;
        if (n.Contains("monthly")) return MonthlyInspection;
        return null;
    }

    /// <summary>Deterministic routine_checks ID, so a restart never creates the same check twice.</summary>
    public static string CheckId(string taxiId, string type, DateTime dueDatePh) =>
        $"{taxiId}_{type}_{dueDatePh:yyyyMMdd}";

    /// <summary>One bell alert per check per status.</summary>
    public static string AlertId(string checkId, string alertType) =>
        $"routine_{checkId}_{(alertType == OverdueAlertType ? "Overdue" : "DueSoon")}";
}

/// <summary>The intervals in effect - system_configs/global, falling back to the defaults for
/// anything unset or not positive.</summary>
public sealed record MaintenanceIntervals(
    int WeeklyDays, int BiweeklyDays, int MonthlyDays, int OilChangeDays, int DueSoonDays,
    IReadOnlyDictionary<string, int> OilChangeMileageKmByUnit)
{
    public static MaintenanceIntervals From(SystemConfig? config)
    {
        static int Or(int value, int fallback) => value > 0 ? value : fallback;
        var mileage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, int> entry in config?.OilChangeMileageIntervalKm ?? new Dictionary<string, int>())
        {
            if (entry.Value > 0) mileage[entry.Key] = entry.Value;
        }

        return new MaintenanceIntervals(
            Or(config?.MaintenanceWeeklyIntervalDays ?? 0, MaintenanceSchedule.DefaultWeeklyIntervalDays),
            Or(config?.MaintenanceBiweeklyIntervalDays ?? 0, MaintenanceSchedule.DefaultBiweeklyIntervalDays),
            Or(config?.MaintenanceMonthlyIntervalDays ?? 0, MaintenanceSchedule.DefaultMonthlyIntervalDays),
            Or(config?.OilChangeIntervalDays ?? 0, MaintenanceSchedule.DefaultOilChangeIntervalDays),
            config is null || config.MaintenanceDueSoonDays < 0 ? MaintenanceSchedule.DefaultDueSoonDays : config.MaintenanceDueSoonDays,
            mileage);
    }

    public int DaysFor(string type) => type switch
    {
        MaintenanceSchedule.WeeklyInspection => WeeklyDays,
        MaintenanceSchedule.BiweeklyCheckup => BiweeklyDays,
        MaintenanceSchedule.MonthlyInspection => MonthlyDays,
        _ => OilChangeDays,
    };

    /// <summary>The unit's oil-change mileage interval, if it has one.</summary>
    public int? OilChangeKmFor(string taxiId) =>
        OilChangeMileageKmByUnit.TryGetValue(taxiId, out int km) ? km : null;
}
