using System;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore.Emergency;

/// <summary>
/// Display names for emergency_alerts.triggerType, shared by ManagerWeb, the SOS push and the
/// manager mobile app so every screen names the three emergency states the same way.
/// </summary>
public static class EmergencyTypes
{
    public const string StandardLabel = "Standard Breakdown";
    public const string HostileLabel = "Hostile Protocol";
    public const string CrashLabel = "Crash Protocol";

    /// <summary>"Standard", "Hostile" or "Crash"; empty or unknown values (older alerts) are Standard.</summary>
    public static string Normalize(string? triggerType) => triggerType?.Trim() switch
    {
        var t when string.Equals(t, EmergencyAlert.Hostile, StringComparison.OrdinalIgnoreCase) => EmergencyAlert.Hostile,
        var t when string.Equals(t, EmergencyAlert.Crash, StringComparison.OrdinalIgnoreCase) => EmergencyAlert.Crash,
        _ => EmergencyAlert.Standard,
    };

    public static string Label(string? triggerType) => Normalize(triggerType) switch
    {
        EmergencyAlert.Hostile => HostileLabel,
        EmergencyAlert.Crash => CrashLabel,
        _ => StandardLabel,
    };

    /// <summary>Higher is more severe - Crash, then Hostile, then Standard.</summary>
    public static int Severity(string? triggerType) => Normalize(triggerType) switch
    {
        EmergencyAlert.Crash => 2,
        EmergencyAlert.Hostile => 1,
        _ => 0,
    };
}
