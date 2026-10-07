using System;
using System.Collections.Generic;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore;

namespace LARGA.SharedCore.Models.Garage;

public class DriverReportEntry
{
    public string MaintenanceId { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;
    public string UnitLabel { get; set; } = string.Empty;
    public string IssueTitle { get; set; } = string.Empty;
    public string IssueDescription { get; set; } = string.Empty;
    public string ReportedByName { get; set; } = string.Empty;
    public DateTime DateLogged { get; set; }
    public PriorityLevel Priority { get; set; }
    public string? SupportingPhotoUrl { get; set; }
}

public class WorkOrderEntry
{
    public string MaintenanceId { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;
    public string UnitLabel { get; set; } = string.Empty;
    public string IssueTitle { get; set; } = string.Empty;
    public string IssueDescription { get; set; } = string.Empty;
    public string ReportedByName { get; set; } = string.Empty;
    public DateTime DateLogged { get; set; }
    public PriorityLevel Priority { get; set; }
    public string? SupportingPhotoUrl { get; set; }
    public string? MechanicInstructions { get; set; }
    public DateTime? EstimatedCompletionDate { get; set; }

    /// <summary>The booked shop day; null on tickets made before scheduling existed.</summary>
    public DateTime? ScheduledDate { get; set; }

    /// <summary>The shop day as a Philippine calendar date.</summary>
    public DateTime ShopDayPh => WorkOrderRules.ShopStartUtc(DateLogged, ScheduledDate).ToPhilippineTime().Date;

    /// <summary>Day 1 = its first day in the shop. Computed, not stored.</summary>
    public int DayNumber => WorkOrderRules.ShopDayNumber(DateLogged, ScheduledDate, PhilippineTime.Now.Date);

    /// <summary>Whole days until a Scheduled ticket's shop day (0 = today).</summary>
    public int DaysUntilShop => Math.Max(0, (int)(ShopDayPh - PhilippineTime.Now.Date).TotalDays);
}

public class RoutineCheckEntry
{
    public string CheckId { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;
    public string UnitLabel { get; set; } = string.Empty;
    public string CheckName { get; set; } = string.Empty;

    /// <summary>Null for a date-based check.</summary>
    public int? DueInKm { get; set; }

    /// <summary>Null for a mileage-based check.</summary>
    public DateTime? DueDate { get; set; }
}

public class HistoryEntry
{
    public string MaintenanceId { get; set; } = string.Empty;
    public string UnitLabel { get; set; } = string.Empty;
    public string IssueTitle { get; set; } = string.Empty;
    public string ReportedByName { get; set; } = string.Empty;
    public DateTime DateLogged { get; set; }
    public DateTime? DateResolved { get; set; }

    /// <summary>"Resolved" or "Dismissed".</summary>
    public string Status { get; set; } = string.Empty;
}

public class GarageSnapshot
{
    public List<DriverReportEntry> PendingReports { get; set; } = new();
    public List<WorkOrderEntry> ActiveWorkOrders { get; set; } = new();

    /// <summary>Tickets booked for a later shop day ("Scheduled"), soonest first.</summary>
    public List<WorkOrderEntry> ScheduledWorkOrders { get; set; } = new();
    public List<RoutineCheckEntry> UpcomingChecks { get; set; } = new();
}

public class GarageActionResult
{
    public bool Ok { get; set; }
    public string? ErrorMessage { get; set; }
}
