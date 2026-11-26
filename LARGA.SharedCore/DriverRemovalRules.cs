using System.Collections.Generic;

namespace LARGA.SharedCore;

/// <summary>When a driver can be deleted (ManagerWeb's driver profile).</summary>
public static class DriverRemovalRules
{
    /// <summary>Why the driver can't be deleted right now; empty when they can.</summary>
    public static List<string> Blockers(bool isOnShift, bool hasPendingClockIn, int unresolvedSosAlerts, decimal amountOwed)
    {
        var blockers = new List<string>();
        if (isOnShift)
        {
            blockers.Add("They're on shift. Wait until they clock out.");
        }
        if (hasPendingClockIn)
        {
            blockers.Add("Their clock-in is waiting for approval. Approve or deny it first.");
        }
        if (unresolvedSosAlerts > 0)
        {
            blockers.Add($"They have {unresolvedSosAlerts} unresolved SOS alert(s). Resolve them in SOS Dispatch first.");
        }
        if (amountOwed > 0)
        {
            blockers.Add($"They still owe ₱{amountOwed:N2}. Settle it (or write it off) in the Financial Ledger first.");
        }
        return blockers;
    }
}
