using LARGA.ManagerWeb.Services;
using LARGA.SharedCore;
using LARGA.SharedCore.Alerts;
using LARGA.SharedCore.Services;

namespace LARGA.Tests.Alerts;

/// <summary>BETA-085: clicking a bell row opens the page that handles it.</summary>
public class AlertLinksTests
{
    [Theory]
    [InlineData(SosDispatchService.SosAlertType, "/sos-dispatch")]
    [InlineData(ClockInApprovalService.PendingAlertType, "/driver-shifts")]
    [InlineData(ShiftDeadlineService.LateReturnAlertType, "/driver-shifts")]
    [InlineData(ShiftDeadlineService.MissedClockOutAlertType, "/driver-shifts")]
    [InlineData(AlertFeed.DriverIdleType, "/dashboard")]
    [InlineData(FuelVerificationService.FuelAlertType, "/fuel-verification")]
    [InlineData(SubmissionAlerts.FuelSubmittedType, "/fuel-verification")]
    [InlineData(SubmissionAlerts.DefectReportedType, "/garage")]
    [InlineData(MaintenanceSchedule.DueSoonAlertType, "/garage")]
    [InlineData(MaintenanceSchedule.OverdueAlertType, "/garage")]
    [InlineData(FinancialLedgerService.UnpaidDebtAlertType, "/financial-ledger")]
    public void SharedPages_SameForBothRoles(string type, string page)
    {
        Assert.Equal(page, AlertLinks.PageFor(type, isManager: true));
        Assert.Equal(page, AlertLinks.PageFor(type, isManager: false));
    }

    [Fact]
    public void LowStock_InventoryForManagerOnly()
    {
        Assert.Equal("/inventory", AlertLinks.PageFor(AlertFeed.LowStockType, isManager: true));
        Assert.Null(AlertLinks.PageFor(AlertFeed.LowStockType, isManager: false));
    }

    [Theory]
    [InlineData(AlertFeed.AccessRequestType)]
    [InlineData("SomethingNew")]
    [InlineData(null)]
    public void NoPage_OnlyMarksRead(string? type)
    {
        Assert.Null(AlertLinks.PageFor(type, isManager: true));
    }

    [Fact]
    public void EveryKnownType_NeverSendsAssistantManagerToManagerOnlyPage()
    {
        foreach (string type in AlertFeed.TypeTitles.Keys)
        {
            string? page = AlertLinks.PageFor(type, isManager: false);
            Assert.NotEqual("/inventory", page);
            Assert.NotEqual("/audit-logs", page);
        }
    }
}
