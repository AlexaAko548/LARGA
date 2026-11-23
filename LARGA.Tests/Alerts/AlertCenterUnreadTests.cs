using LARGA.SharedCore.Alerts;
using LARGA.SharedCore.Services;
using BellRow = LARGA.SharedCore.Alerts.AlertCenterUnread.BellRow;

namespace LARGA.Tests.Alerts;

/// <summary>The Alert Center's Unread count - shared by the page and the Alerts tab badge (AlertCenterUnread).</summary>
public class AlertCenterUnreadTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 2, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Every_pending_action_card_counts_whatever_its_bell_row_says()
    {
        // An SOS whose web bell row was already opened (isRead) is still unread on the phone until resolved.
        var bell = new[] { new BellRow(AlertFeed.SosBellId("sos1"), SosDispatchService.SosAlertType, true, Now) };

        Assert.Equal(3, AlertCenterUnread.Count(new[] { "sos1", "fuel1", "req1" }, bell, Now));
    }

    [Fact]
    public void A_bell_row_an_action_card_stands_for_is_not_counted_twice()
    {
        var bell = new[]
        {
            new BellRow(AlertFeed.SosBellId("sos1"), SosDispatchService.SosAlertType, false, Now),
            new BellRow(AlertFeed.ClockInBellId("req1"), ClockInApprovalService.PendingAlertType, false, Now),
        };

        Assert.Equal(2, AlertCenterUnread.Count(new[] { "sos1", "req1" }, bell, Now));
    }

    [Fact]
    public void Unread_notices_count_read_ones_do_not()
    {
        var bell = new[]
        {
            new BellRow("n1", "MaintenanceDue", false, Now.AddHours(-1)),
            new BellRow("n2", "MaintenanceDue", true, Now.AddHours(-1)),
            new BellRow("n3", AlertFeed.DriverIdleType, false, Now.AddDays(-20)), // old but never read: still unread
        };

        Assert.Equal(2, AlertCenterUnread.Count(Array.Empty<string>(), bell, Now));
    }

    [Fact]
    public void Web_only_notices_are_not_counted_on_the_phone()
    {
        var bell = new[] { new BellRow("f1", SubmissionAlerts.FuelSubmittedType, false, Now) };

        Assert.False(AlertFeed.ShownOnPhone(SubmissionAlerts.FuelSubmittedType));
        Assert.Equal(0, AlertCenterUnread.Count(Array.Empty<string>(), bell, Now));
    }

    [Fact]
    public void A_bell_row_whose_card_was_handled_counts_as_a_notice_again()
    {
        // The SOS was resolved (no card any more); its bell row is now an ordinary notice.
        var bell = new[] { new BellRow(AlertFeed.SosBellId("sos1"), SosDispatchService.SosAlertType, false, Now) };

        Assert.Equal(1, AlertCenterUnread.Count(Array.Empty<string>(), bell, Now));
    }

    [Fact]
    public void Old_read_notices_are_hidden_from_the_list()
    {
        var cards = new HashSet<string>();
        Assert.True(AlertCenterUnread.ShowsBellRow(new BellRow("n1", "Notice", true, Now.AddDays(-1)), cards, Now));
        Assert.False(AlertCenterUnread.ShowsBellRow(new BellRow("n1", "Notice", true, Now - AlertFeed.HideReadAfter - TimeSpan.FromMinutes(1)), cards, Now));
    }

    [Fact]
    public void Nothing_pending_is_zero()
    {
        Assert.Equal(0, AlertCenterUnread.Count(Array.Empty<string>(), Array.Empty<BellRow>(), Now));
    }
}
