using System;

namespace LARGA.SharedCore.Models.Alerts;

/// <summary>One taxi/driver currently computed as "Idle" (see FleetReportingService's live
/// fleet-status precedence) - just enough identity to raise/render an alert about it.</summary>
public class IdleDriverInfo
{
    public string DriverId { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;
    public string UnitLabel { get; set; } = string.Empty;
    public string ShiftId { get; set; } = string.Empty;
    public double IdleThresholdMinutes { get; set; }
}

/// <summary>A system_alerts row shaped for display - ManagerWeb's notification bell and the
/// mobile Alert Center both consume this same shape via AlertService.</summary>
public class AlertNotice
{
    public string AlertId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string UnitLabel { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public bool IsRead { get; set; }
}
