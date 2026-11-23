using LARGA.SharedCore.Alerts;

namespace LARGA.Tests.Alerts;

/// <summary>SRS REQ-4.3-7 / REQ-4.4-8: a manager alert for every defect report and fuel submission.</summary>
public class SubmissionAlertsTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Ids_AreDeterministicPerSourceDocument()
    {
        Assert.Equal("defect_m1", SubmissionAlerts.DefectAlertId("m1"));
        Assert.Equal("fuelsub_f1", SubmissionAlerts.FuelSubmittedAlertId("f1"));
        Assert.NotEqual(AlertFeed.FuelBellId("f1"), SubmissionAlerts.FuelSubmittedAlertId("f1"));
    }

    [Fact]
    public void BothTypes_HaveBellTitles()
    {
        Assert.Equal("Defect report", AlertFeed.TitleFor(SubmissionAlerts.DefectReportedType));
        Assert.Equal("Fuel submission", AlertFeed.TitleFor(SubmissionAlerts.FuelSubmittedType));
    }

    [Fact]
    public void Phone_ListsDefectReports_ButNotEveryFuelSubmission()
    {
        Assert.True(AlertFeed.ShownOnPhone(SubmissionAlerts.DefectReportedType));
        Assert.False(AlertFeed.ShownOnPhone(SubmissionAlerts.FuelSubmittedType));
        Assert.True(AlertFeed.ShownOnPhone(AlertFeed.LowStockType));
        Assert.True(AlertFeed.ShownOnPhone(null));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(23.9, true)]
    [InlineData(24.1, false)]
    public void FirstRun_OnlyAlertsOnTheLast24Hours(double hoursAgo, bool expected) =>
        Assert.Equal(expected, SubmissionAlerts.IsRecent(Now.AddHours(-hoursAgo), Now));

    [Fact]
    public void Messages()
    {
        Assert.Equal("Dan A reported \"Flat tire\" on TX-01 (High priority).",
            SubmissionAlerts.DefectMessage("Dan A", "TX-01", " Flat tire ", "High"));
        Assert.Equal("Dan A reported a defect on TX-01.", SubmissionAlerts.DefectMessage("Dan A", "TX-01", "", null));
        Assert.Equal("Dan A submitted a fuel report for TX-01: 30.5 L, PHP 1,950.00 at Petron.",
            SubmissionAlerts.FuelMessage("Dan A", "TX-01", 30.5, 1950, "Petron"));
    }
}
