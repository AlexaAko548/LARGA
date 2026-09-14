using System;
using System.Collections.Generic;
using LARGA.Shared.Models.Entities;

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

    /// <summary>Day 1 = ticket created today. Computed from DateLogged, not stored.</summary>
    public int DayNumber => Math.Max(1, (int)(DateTime.UtcNow.Date - DateLogged.Date).TotalDays + 1);
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
    public List<RoutineCheckEntry> UpcomingChecks { get; set; } = new();
}

public class GarageActionResult
{
    public bool Ok { get; set; }
    public string? ErrorMessage { get; set; }
}
