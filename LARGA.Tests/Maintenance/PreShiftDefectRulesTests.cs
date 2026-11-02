using LARGA.Shared.Models.Entities;
using LARGA.SharedCore;

namespace LARGA.Tests.Maintenance;

/// <summary>A pre-shift defect report stays out of the Garage's Pending Driver Reports only while its clock-in
/// request is pending, and joins it once the manager decides (approved or denied).</summary>
public class PreShiftDefectRulesTests
{
    private static MaintenanceRecord Reported(string id) => new() { MaintenanceId = id, Status = WorkOrderRules.Reported };

    private static List<string> Pending(IEnumerable<MaintenanceRecord> records, params (string Status, object? Ids)[] requests) =>
        PreShiftDefectRules.PendingGarageReports(records,
                PreShiftDefectRules.HeldDefectIds(requests.Select(r => ((string?)r.Status, r.Ids))))
            .Select(r => r.MaintenanceId)
            .ToList();

    [Fact]
    public void PendingRequest_Hidden()
    {
        Assert.Empty(Pending(new[] { Reported("m1") }, (ClockInRequest.Pending, "m1")));
    }

    // The unit went out anyway, but the defect still needs scheduling or dismissing - and Driver & Shifts no
    // longer lists the request, so the Garage is the only place left to see it.
    [Fact]
    public void ApprovedRequest_Shown_AlsoOnceClockedIn()
    {
        Assert.Equal(new[] { "m1" }, Pending(new[] { Reported("m1") }, (ClockInRequest.Approved, "m1")));
        Assert.Equal(new[] { "m1" }, Pending(new[] { Reported("m1") }, (ClockInRequest.ClockedIn, "m1")));
    }

    [Fact]
    public void OnlyPendingRequestsHoldDefects()
    {
        Assert.Equal(new[] { ClockInRequest.Pending }, PreShiftDefectRules.HeldStatuses);
    }

    [Fact]
    public void DeniedRequest_Shown()
    {
        Assert.Equal(new[] { "m1" }, Pending(new[] { Reported("m1") }, (ClockInRequest.Denied, "m1")));
    }

    [Fact]
    public void CancelledRequest_Shown()
    {
        Assert.Equal(new[] { "m1" }, Pending(new[] { Reported("m1") }, (ClockInRequest.Cancelled, "m1")));
    }

    [Fact]
    public void PostShiftReport_NoClockInLink_Shown()
    {
        var postShift = new MaintenanceRecord { MaintenanceId = "m9", Status = WorkOrderRules.Reported, ShiftId = "shift-1" };
        Assert.Equal(new[] { "m9" }, Pending(new[] { postShift, Reported("m1") }, (ClockInRequest.Pending, "m1")));
    }

    [Fact]
    public void OnlyReportedRecordsListed()
    {
        var ticketed = new MaintenanceRecord { MaintenanceId = "m2", Status = WorkOrderRules.InProgress };
        Assert.Equal(new[] { "m1" }, Pending(new[] { Reported("m1"), ticketed }));
    }

    [Fact]
    public void DefectIds_ParsedSafely()
    {
        Assert.Equal(new[] { "a", "b", "c" }, PreShiftDefectRules.DefectIds(" a, b ,,c ,"));
        Assert.Equal(new[] { "a", "b" }, PreShiftDefectRules.DefectIds(new List<object> { "a", " ", "b" }));
        Assert.Empty(PreShiftDefectRules.DefectIds(null));
        Assert.Empty(PreShiftDefectRules.DefectIds(""));
        Assert.Empty(PreShiftDefectRules.DefectIds(42L));
    }

    [Fact]
    public void SeveralDefectsOnOneRequest_AllHidden()
    {
        Assert.Equal(new[] { "m3" },
            Pending(new[] { Reported("m1"), Reported("m2"), Reported("m3") }, (ClockInRequest.Pending, "m1,m2")));
    }
}
