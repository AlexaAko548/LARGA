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

    /// <summary>On shift, but the driver has paused it from the mobile app.</summary>
    public bool IsOnBreak { get; set; }

    /// <summary>On shift and past the unit's 10:00 PM return time (ShiftRules).</summary>
    public bool IsLateReturn { get; set; }

    /// <summary>Balance unpaid for ShiftRules.DebtFlagDays+ days (users.debtFlaggedSince).</summary>
    public bool IsDebtFlagged { get; set; }

    /// <summary>The driver's permanent unit (their profile's assignment).</summary>
    public string? AssignedTaxiId { get; set; }

    /// <summary>The unit on their current Active shift, when on shift - can differ from
    /// AssignedTaxiId (a substitute, or a shift started before a reassignment).</summary>
    public string? CurrentShiftTaxiId { get; set; }
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

    /// <summary>The driver's permanently assigned unit, or null when they're off this day
    /// or have no unit assigned. One driver = one unit, permanently - there is no per-day
    /// reassignment.</summary>
    public string? TaxiId { get; set; }

    /// <summary>True when this day was explicitly marked off (a "DayOff" exception exists
    /// for this driver/date), as opposed to just defaulting to a working day.</summary>
    public bool IsDayOff { get; set; }

    /// <summary>True when the driver's license makes them ineligible to be scheduled on
    /// this specific date - either this date falls on/after their license's expiry, or
    /// within one month before it (mirrors the same 1-month cutoff used for "On Shift"
    /// eligibility elsewhere). The cell is locked (no unit, no day-off toggle) when true.</summary>
    public bool IsLicenseIneligible { get; set; }

    /// <summary>The driver's own assigned unit is in the shop on this date (taxi status
    /// "Under Maintenance", or an In Progress Garage job covering the date). Without a
    /// substitute, the driver has nothing to drive - TaxiId is null.</summary>
    public bool IsOwnUnitUnderMaintenance { get; set; }

    /// <summary>A temporary unit the manager assigned for just this date while the driver's
    /// own unit is under maintenance. When set, TaxiId is this unit.</summary>
    public string? SubstituteTaxiId { get; set; }

    /// <summary>Why the driver's own unit is out, for the cell's tooltip / substitute window
    /// (e.g. "Brake repair - until Oct 3").</summary>
    public string? MaintenanceNote { get; set; }
}

public class DriverScheduleRow
{
    public string DriverId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public LicenseStatus LicenseStatus { get; set; }

    /// <summary>The driver's permanently assigned unit (from their profile), or null if
    /// none is set. Every day in this row defaults to this unit unless overridden.</summary>
    public string? DefaultTaxiId { get; set; }

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

    /// <summary>Per day (Monday through Sunday), the units a substitute can be picked from:
    /// not under maintenance and not driven by anyone else that day.</summary>
    public List<List<string>> FreeTaxiIdsByDay { get; set; } = new();
}

public class ShiftLogEntry
{
    /// <summary>The shift's Firestore document ID.</summary>
    public string ShiftId { get; set; } = string.Empty;
    public DateTime? ShiftStart { get; set; }
    public string DriverId { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;

    /// <summary>ShiftLog.Status (e.g. Active, Completed, Overdue), except an Active shift the
    /// driver has paused shows as "On Break".</summary>
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

    /// <summary>The taxi unit's most recent past inspection checklists (other shifts),
    /// newest first - reference material for the manager reviewing this shift's checklist,
    /// not this shift's own Pre/End-Shift data above.</summary>
    public List<ChecklistHistoryEntry> PreviousChecklists { get; set; } = new();

    /// <summary>The taxi unit's past damage/accident maintenance records, newest first.</summary>
    public List<DamageHistoryEntry> DamageHistory { get; set; } = new();
}

public class ChecklistHistoryEntry
{
    public DateTime Timestamp { get; set; }

    /// <summary>"Pre-Shift" or "Post-Shift" (display label).</summary>
    public string ChecklistType { get; set; } = string.Empty;

    public int PassedCount { get; set; }
    public int TotalCheckableCount { get; set; }
    public bool AllPassed => TotalCheckableCount > 0 && PassedCount == TotalCheckableCount;
}

public class DamageHistoryEntry
{
    public DateTime DateLogged { get; set; }
    public string IssueTitle { get; set; } = string.Empty;

    /// <summary>Raw MaintenanceRecord.Status (Reported/InProgress/Resolved/Dismissed).</summary>
    public string Status { get; set; } = string.Empty;
}

public class DriverProfileDetail
{
    public string DriverId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;

    /// <summary>The email the driver signs in to the mobile app with.</summary>
    public string Email { get; set; } = string.Empty;

    public string PhoneNumber { get; set; } = string.Empty;
    public string? Address { get; set; }
    public DateTime? DateJoined { get; set; }
    public bool IsOnShift { get; set; }

    /// <summary>The driver's permanent unit (their profile's assignment).</summary>
    public string? AssignedTaxiId { get; set; }

    /// <summary>The unit on their current Active shift, if any.</summary>
    public string? CurrentShiftTaxiId { get; set; }

    public string LicenseNumber { get; set; } = string.Empty;
    public string LicenseClassification { get; set; } = string.Empty;
    public string LicenseRestrictionCode { get; set; } = string.Empty;
    public LicenseStatus LicenseStatus { get; set; }
    public DateTime? LicenseExpiryDate { get; set; }
    public string? LtoIdPhotoUrl { get; set; }
    /// <summary>Attendance, punctuality, payment reliability and damage incidents over the last
    /// DriverManagementService.PerformanceWindowDays days.</summary>
    public LARGA.SharedCore.Services.DriverPerformance Performance { get; set; } = new();
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

/// <summary>A flagged pre-shift inspection waiting for the manager (clockin_requests).</summary>
public class ClockInApprovalItem
{
    public string RequestId { get; set; } = string.Empty;
    public string DriverId { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public List<string> FlagReasons { get; set; } = new();

    /// <summary>A checklist item failed (vs. only low fuel) - denying then escalates to maintenance.</summary>
    public bool HasFailedItems { get; set; }

    public bool IsBelowHalfTank { get; set; }
    public int StartMileage { get; set; }
    public string? FuelPhotoUrl { get; set; }
    public string? OdometerPhotoUrl { get; set; }
    public List<ClockInDefect> Defects { get; set; } = new();
}

public class ClockInDefect
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
}

public class ClockInDecisionResult
{
    public bool Ok { get; set; }
    public string? ErrorMessage { get; set; }
}
