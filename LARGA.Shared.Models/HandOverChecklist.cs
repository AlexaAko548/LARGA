namespace LARGA.Shared.Models;

public enum ChecklistType
{
    PreShift,
    EndShift
}

public enum FuelVerificationLevel
{
    HalfTank,
    BelowHalfTank
}

public class HandoverChecklist
{
    public string ChecklistId { get; set; } = string.Empty;
    public string ShiftId { get; set; } = string.Empty;
    public ChecklistType ChecklistType { get; set; }
    public bool TireCondition { get; set; }
    public bool OilLevel { get; set; }
    public bool CoolantLevel { get; set; }
    public bool InteriorCleanliness { get; set; }
    public bool ExteriorScratches { get; set; }
    public FuelVerificationLevel FuelVerification { get; set; }
    public string? ScratchesPhotoUrl { get; set; }
    public string? FuelDashboardUrl { get; set; }
    public DateTime Timestamp { get; set; }
}