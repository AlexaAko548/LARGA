using LARGA.SharedCore.Push;
using Device = LARGA.SharedCore.Push.PushTokenRules.DeviceToken;

namespace LARGA.Tests.Push;

/// <summary>Which device a push may go to, and which token copies ManagerWeb removes (PushTokenRules).</summary>
public class PushTokenRulesTests
{
    private static readonly DateTime T0 = new(2026, 11, 15, 1, 0, 0, DateTimeKind.Utc);

    private static Device Android(string uid, string token, DateTime? at = null) => new(uid, token, PushTokenRules.AndroidPlatform, at ?? T0);
    private static Device Web(string uid, string token, DateTime? at = null) => new(uid, token, PushTokenRules.WebPlatform, at ?? T0);

    [Fact]
    public void Doc_id_is_the_upper_case_sha256_of_the_token()
    {
        // The same value firestore.rules computes: hashing.sha256(token).toHexString().
        Assert.Equal("2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824", PushTokenRules.DocId("hello"));
        Assert.Equal(PushTokenRules.DocId("hello"), PushTokenRules.DocId(" hello "));
        Assert.Equal(64, PushTokenRules.DocId("fcm:APA91b-very/long_token").Length);
    }

    [Fact]
    public void Only_the_recipients_own_devices_are_used()
    {
        var all = new[] { Android("drvA", "a"), Android("drvB", "b"), Web("mgr", "m-web"), Android("mgr", "m-phone") };

        Assert.Equal(new[] { "a" }, PushTokenRules.Deliverable(all, new[] { "drvA" }).Select(d => d.Token));
        Assert.Equal(new[] { "m-web", "m-phone" }, PushTokenRules.Deliverable(all, new[] { "mgr" }).Select(d => d.Token));
        Assert.Empty(PushTokenRules.Deliverable(all, Array.Empty<string>()));
    }

    [Fact]
    public void A_shared_phone_belongs_to_whoever_registered_it_last()
    {
        var all = new[] { Android("drvA", "phone", T0), Android("drvB", "phone", T0.AddHours(2)) };

        Assert.Empty(PushTokenRules.Deliverable(all, new[] { "drvA" }));
        Device b = Assert.Single(PushTokenRules.Deliverable(all, new[] { "drvA", "drvB" }));
        Assert.Equal("drvB", b.Uid);
    }

    [Fact]
    public void A_tie_goes_to_no_one()
    {
        var all = new[] { Android("drvA", "phone", T0), Android("drvB", "phone", T0) };
        Assert.Empty(PushTokenRules.Deliverable(all, new[] { "drvA", "drvB" }));
        Assert.Empty(PushTokenRules.Superseded(all));
    }

    [Fact]
    public void A_token_also_under_the_excluded_sender_is_skipped()
    {
        var all = new[] { Android("mgr", "phone", T0), Android("asst1", "phone", T0.AddMinutes(1)), Android("asst1", "other") };
        Assert.Equal(new[] { "other" }, PushTokenRules.Deliverable(all, new[] { "asst1" }, excludeUid: "mgr").Select(d => d.Token));
    }

    [Fact]
    public void Platforms_can_be_limited()
    {
        var all = new[] { Android("mgr", "phone"), Web("mgr", "browser") };
        Assert.Equal(new[] { "phone" },
            PushTokenRules.Deliverable(all, new[] { "mgr" }, platforms: p => p == PushTokenRules.AndroidPlatform).Select(d => d.Token));
    }

    [Fact]
    public void Superseded_lists_the_older_copies_only()
    {
        var all = new[]
        {
            Android("drvA", "phone", T0),
            Android("drvB", "phone", T0.AddHours(1)),
            Android("drvC", "phone", T0.AddHours(3)),
            Android("drvA", "own"),
        };

        Assert.Equal(new[] { "drvA", "drvB" }, PushTokenRules.Superseded(all).Select(d => d.Uid));
    }

    [Fact]
    public void Blank_tokens_and_uids_are_ignored()
    {
        var all = new[] { Android("drvA", " "), Android("", "x"), Android("drvA", "ok") };
        Assert.Equal(new[] { "ok" }, PushTokenRules.Deliverable(all, new[] { "drvA", "" }).Select(d => d.Token));
    }

    [Theory]
    [InlineData("drvA", "drvA", true)]
    [InlineData("drvA", " drvA ", true)]
    [InlineData("drvA", "drvB", false)]
    [InlineData("drvA", "DRVA", false)]
    [InlineData("drvA", null, false)]
    [InlineData(null, "drvA", false)]
    [InlineData("", "", false)]
    public void A_push_is_shown_only_to_the_signed_in_recipient(string? recipient, string? signedIn, bool shown)
    {
        var data = recipient is null
            ? new Dictionary<string, string> { ["type"] = "chat_message" }
            : new Dictionary<string, string> { ["type"] = "chat_message", [PushTokenRules.RecipientUidKey] = recipient };

        Assert.Equal(shown, PushTokenRules.IsForSignedInUser(data, signedIn));
    }

    [Fact]
    public void A_push_with_no_data_is_never_shown()
    {
        Assert.False(PushTokenRules.IsForSignedInUser(null, "drvA"));
    }
}
