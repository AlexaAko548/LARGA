using LARGA.SharedCore.Alerts;

namespace LARGA.Tests.Alerts;

/// <summary>Read notifications drop off the web bell and the phone's Alert Center after a week; unread ones never do.</summary>
public class AlertFeedVisibilityTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ReadRow_OlderThanAWeek_IsHidden()
    {
        Assert.True(AlertFeed.IsHiddenRead(isRead: true, Now.AddDays(-8), Now));
        Assert.True(AlertFeed.IsHiddenRead(isRead: true, Now.AddDays(-7).AddSeconds(-1), Now));
    }

    [Fact]
    public void ReadRow_WithinAWeek_IsShown()
    {
        Assert.False(AlertFeed.IsHiddenRead(isRead: true, Now.AddDays(-7), Now));
        Assert.False(AlertFeed.IsHiddenRead(isRead: true, Now.AddHours(-1), Now));
    }

    [Fact]
    public void UnreadRow_IsNeverHidden()
    {
        Assert.False(AlertFeed.IsHiddenRead(isRead: false, Now.AddDays(-90), Now));
    }
}
