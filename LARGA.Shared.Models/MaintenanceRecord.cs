namespace LARGA.Shared.Models;

public enum MaintenanceType
{
    RoutineCheckup,
    BreakdownRepair,
    AccidentCorrection
}

public enum PriorityLevel
{
    Low,
    Medium,
    High
}

public class MaintenanceRecord
{
    public string MaintenanceId { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;
    public string ManagerId { get; set; } = string.Empty;
    public string ShiftId { get; set; } = string.Empty;
    public MaintenanceType MaintenanceType { get; set; }
    public string IssueTitle { get; set; } = string.Empty;
    public string? IssueDescription { get; set; }
    public DateTime DateLogged { get; set; }
    public DateTime? DateResolved { get; set; }
    public decimal? LaborCost { get; set; }
    public decimal? TotalCost { get; set; }
    public string? SupportingPhotoUrl { get; set; }
    public PriorityLevel PriorityLevel { get; set; }
}