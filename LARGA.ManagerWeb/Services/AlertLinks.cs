using LARGA.SharedCore;
using LARGA.SharedCore.Alerts;
using LARGA.SharedCore.Services;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// BETA-085: the dashboard page that handles a notification bell row, so clicking it takes the manager
/// there. Null when no page does (an access request is decided in the profile menu's Team &amp; Access panel,
/// not a page) or the type isn't known - the click then only marks it read.
/// </summary>
public static class AlertLinks
{
    public static string? PageFor(string? type, bool isManager) => type switch
    {
        SosDispatchService.SosAlertType => "/sos-dispatch",
        ClockInApprovalService.PendingAlertType => "/driver-shifts",
        ShiftDeadlineService.LateReturnAlertType => "/driver-shifts",
        ShiftDeadlineService.MissedClockOutAlertType => "/driver-shifts",
        AlertFeed.DriverIdleType => "/dashboard",
        FuelVerificationService.FuelAlertType => "/fuel-verification",
        SubmissionAlerts.FuelSubmittedType => "/fuel-verification",
        SubmissionAlerts.DefectReportedType => "/garage",
        MaintenanceSchedule.DueSoonAlertType => "/garage",
        MaintenanceSchedule.OverdueAlertType => "/garage",
        FinancialLedgerService.UnpaidDebtAlertType => "/financial-ledger",
        // Inventory is Manager-only, and no other page shows stock levels.
        AlertFeed.LowStockType => isManager ? "/inventory" : null,
        _ => null,
    };
}
