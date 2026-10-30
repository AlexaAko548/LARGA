using LARGA.SharedCore.Alerts;
using LARGA.SharedCore.Services;

namespace LARGA.Tests.Alerts;

/// <summary>Access requests (ManagerWeb's /register): a web-bell notice for the full Manager only.</summary>
public class AccessRequestAlertTests
{
    [Fact]
    public void HasBellTitle()
    {
        Assert.Equal("Access request", AlertFeed.TitleFor(AlertFeed.AccessRequestType));
    }

    [Fact]
    public void NotListedOnPhone()
    {
        Assert.False(AlertFeed.ShownOnPhone(AlertFeed.AccessRequestType));
    }

    [Fact]
    public void ManagerOnly_JustAccessRequests()
    {
        Assert.True(AlertFeed.ManagerOnly(AlertFeed.AccessRequestType));
        Assert.False(AlertFeed.ManagerOnly(AlertFeed.LowStockType));
        Assert.False(AlertFeed.ManagerOnly(SosDispatchService.SosAlertType));
        Assert.False(AlertFeed.ManagerOnly(null));
    }

    [Fact]
    public void PendingLogin_SignInMessage_MentionsTheRequest()
    {
        Assert.Contains("access request", FirebaseAuthService.DisabledAccountMessage);
    }
}
