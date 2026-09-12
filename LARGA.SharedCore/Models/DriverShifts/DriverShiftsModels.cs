using System;
using System.Collections.Generic;
using System.Linq;

namespace LARGA.SharedCore.Models.DriverShifts;

public enum LicenseStatus
{
    NotSet,
    Valid,
    Expiring,
    Expired,
}

public class DriverRosterEntry
{
    public string DriverId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public LicenseStatus LicenseStatus { get; set; }
    public bool IsOnShift { get; set; }
    public string? AssignedTaxiId { get; set; }
}

public class RosterSnapshot
{
    public int TotalDrivers { get; set; }
    public int OnShiftCount { get; set; }
    public int OffDutyCount { get; set; }
    public int ExpiredLicenseCount { get; set; }
    public List<DriverRosterEntry> Drivers { get; set; } = new();
}

public class ScheduleDayCell
{
    public DateTime Date { get; set; }

    /// <summary>Assigned taxi for this driver/day, or null for a rest day.</summary>
    public string? TaxiId { get; set; }
}

public class DriverScheduleRow
{
    public string DriverId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public LicenseStatus LicenseStatus { get; set; }

    /// <summary>Always 7 entries, Monday through Sunday.</summary>
    public List<ScheduleDayCell> Days { get; set; } = new();
}

public class WeekSchedule
{
    /// <summary>Monday of the displayed week.</summary>
    public DateTime WeekStart { get; set; }

    public List<DriverScheduleRow> Rows { get; set; } = new();

    /// <summary>Count of taxis assigned that day across all drivers - 7 entries, Monday through Sunday.</summary>
    public List<int> UnitsActiveByDay { get; set; } = new();

    public int TotalTaxis { get; set; }
    public List<string> TaxiIds { get; set; } = new();
}

public class ShiftLogEntry
{
    public string ShiftId { get; set; } = string.Empty;
    public DateTime? ShiftStart { get; set; }
    public string DriverId { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;

    /// <summary>Raw ShiftLog.Status (e.g. Active, Completed, Overdue).</summary>
    public string Status { get; set; } = string.Empty;

    public bool HasPreShiftChecklist { get; set; }
    public bool HasEndShiftChecklist { get; set; }
    public int DefectCount { get; set; }
}

public class ChecklistItemResult
{
    public string Label { get; set; } = string.Empty;

    /// <summary>Null for an informational (not pass/fail) reading, e.g. fuel level.</summary>
    public bool? Passed { get; set; }

    public string? ValueText { get; set; }
}

public class ChecklistDetail
{
    /// <summary>"Pre-Shift" or "Post-Shift" (display label).</summary>
    public string ChecklistType { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; }
    public List<ChecklistItemResult> Items { get; set; } = new();

    public string? PrimaryPhotoUrl { get; set; }
    public string? PrimaryPhotoLabel { get; set; }
    public string? SecondaryPhotoUrl { get; set; }
    public string? SecondaryPhotoLabel { get; set; }

    public int PassedCount => Items.Count(i => i.Passed == true);
    public int TotalCheckableCount => Items.Count(i => i.Passed.HasValue);
    public bool AllPassed => TotalCheckableCount > 0 && PassedCount == TotalCheckableCount;
}

public class ShiftChecklists
{
    public string ShiftId { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public ChecklistDetail? PreShift { get; set; }
    public ChecklistDetail? EndShift { get; set; }
}

public class DriverProfileDetail
{
    public string DriverId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Address { get; set; }
    public DateTime? DateJoined { get; set; }
    public bool IsOnShift { get; set; }
    public string? AssignedTaxiId { get; set; }
    public string LicenseNumber { get; set; } = string.Empty;
    public string LicenseClassification { get; set; } = string.Empty;
    public string LicenseRestrictionCode { get; set; } = string.Empty;
    public LicenseStatus LicenseStatus { get; set; }
    public DateTime? LicenseExpiryDate { get; set; }
    public string? LtoIdPhotoUrl { get; set; }
    public double PunctualPercent { get; set; }
    public double PaymentReliabilityPercent { get; set; }
    public int DamageIncidentCount { get; set; }
    public string? ManagerNote { get; set; }
}

public class CreateDriverResult
{
    public bool Ok { get; set; }
    public string? ErrorMessage { get; set; }
    public string? DriverId { get; set; }
    public string? GeneratedEmail { get; set; }
    public string? TemporaryPassword { get; set; }
}

public class ResetPasswordResult
{
    public bool Ok { get; set; }
    public string? ErrorMessage { get; set; }
    public string? NewPassword { get; set; }
}
