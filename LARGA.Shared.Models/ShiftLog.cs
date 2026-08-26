namespace LARGA.Shared.Models;

public enum ShiftStatus
{
    Active,
    Completed,
    Overdue
}

public class ShiftLog
{
    public string ShiftId { get; set; } = string.Empty;
    public string DriverId { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;
    public DateTime ShiftStart { get; set; }
    public DateTime? ShiftEnd { get; set; }
    public ShiftStatus Status { get; set; }
    public int StartMileage { get; set; }
    public int? EndMileage { get; set; }
    public string? ManagerNote { get; set; }
}