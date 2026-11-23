using LARGA.SharedCore.Chats;
using LARGA.SharedCore.Push;
using User = LARGA.SharedCore.Chats.ChatPushPolicy.User;
using Device = LARGA.SharedCore.Push.PushTokenRules.DeviceToken;
using Message = LARGA.SharedCore.Chats.ChatPushPolicy.Message;

namespace LARGA.Tests.Chats;

/// <summary>Who ManagerWeb's ChatPushService pushes a new chat message to (ChatPushPolicy).</summary>
public class ChatPushPolicyTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 2, 0, 0, DateTimeKind.Utc);

    // Driver A and B, a Manager and two Assistant Managers (roles written the ways the apps accept).
    private static readonly User DriverA = new("drvA", "Driver");
    private static readonly User DriverB = new("drvB", " driver ");
    private static readonly User Manager = new("mgr", "Manager");
    private static readonly User Asst1 = new("asst1", "Assistant Manager");
    private static readonly User Asst2 = new("asst2", "assistant manager");
    private static readonly User[] Everyone = { DriverA, DriverB, Manager, Asst1, Asst2 };

    // One registered phone each (users/{uid}/fcmTokens), the manager also signed in on the web.
    private static Device Phone(string uid, string token, DateTime? at = null) => new(uid, token, PushTokenRules.AndroidPlatform, at ?? T0);
    private static readonly Device[] Devices =
    {
        Phone("drvA", "tok-drvA"), Phone("drvB", "tok-drvB"), Phone("mgr", "tok-mgr"),
        Phone("asst1", "tok-asst1"), Phone("asst2", "tok-asst2"),
        new("mgr", "tok-mgr-web", PushTokenRules.WebPlatform, T0),
    };

    private static string[] Tokens(IEnumerable<Device> devices) => devices.Select(d => d.Token).ToArray();

    private static Message FromDriver(string driverId) => new(driverId, true, null, null);
    private static Message FromStaff(string driverId, string senderId, string role = "Manager") => new(driverId, false, senderId, role);

    [Fact]
    public void Driver_message_goes_to_every_manager_and_assistant_manager()
    {
        Assert.Equal(new[] { "mgr", "asst1", "asst2" }, ChatPushPolicy.RecipientUids(FromDriver("drvA"), Everyone));
        Assert.Equal(new[] { "tok-mgr", "tok-asst1", "tok-asst2", "tok-mgr-web" },
            Tokens(ChatPushPolicy.RecipientDevices(FromDriver("drvA"), Everyone, Devices)));
    }

    [Fact]
    public void Staff_message_goes_to_that_driver_and_the_other_staff_never_the_sender()
    {
        IReadOnlyList<string> to = ChatPushPolicy.RecipientUids(FromStaff("drvA", "asst1", "Assistant Manager"), Everyone);

        Assert.Equal(new[] { "drvA", "mgr", "asst2" }, to);
        Assert.DoesNotContain("asst1", to);
    }

    [Fact]
    public void A_driver_never_hears_about_another_drivers_conversation()
    {
        Assert.DoesNotContain("drvB", ChatPushPolicy.RecipientUids(FromStaff("drvA", "mgr"), Everyone));
        Assert.DoesNotContain("drvB", ChatPushPolicy.RecipientUids(FromDriver("drvA"), Everyone));
        Assert.DoesNotContain("tok-drvB", Tokens(ChatPushPolicy.RecipientDevices(FromStaff("drvA", "mgr"), Everyone, Devices)));
        Assert.DoesNotContain("tok-drvB", Tokens(ChatPushPolicy.RecipientDevices(FromDriver("drvA"), Everyone, Devices)));
    }

    [Fact]
    public void Driver_is_not_notified_of_their_own_message()
    {
        Assert.DoesNotContain("drvA", ChatPushPolicy.RecipientUids(FromDriver("drvA"), Everyone));
    }

    [Theory]
    [InlineData("System")]
    [InlineData(" system ")]
    public void System_notices_push_nobody(string role)
    {
        // DriverNotifier already pushes these to the driver.
        var notice = new Message("drvA", false, "system", role);
        Assert.Empty(ChatPushPolicy.RecipientUids(notice, Everyone));
        Assert.Empty(ChatPushPolicy.RecipientDevices(notice, Everyone, Devices));
    }

    [Fact]
    public void Sender_is_not_notified_on_any_of_their_devices()
    {
        string[] tokens = Tokens(ChatPushPolicy.RecipientDevices(FromStaff("drvA", "mgr"), Everyone, Devices));

        Assert.DoesNotContain("tok-mgr", tokens);
        Assert.DoesNotContain("tok-mgr-web", tokens);
        Assert.Equal(new[] { "tok-drvA", "tok-asst1", "tok-asst2" }, tokens);
    }

    [Fact]
    public void Every_device_belongs_to_a_participant_who_is_not_the_sender()
    {
        // Each device's uid becomes that push's recipientUid (PushTokenStore.WithRecipient).
        Message message = FromStaff("drvB", "asst2", "Assistant Manager");
        IReadOnlyList<string> participants = ChatPushPolicy.RecipientUids(message, Everyone);

        IReadOnlyList<Device> devices = ChatPushPolicy.RecipientDevices(message, Everyone, Devices);

        Assert.All(devices, d => Assert.Contains(d.Uid, participants));
        Assert.DoesNotContain(devices, d => d.Uid is "asst2" or "drvA");
    }

    [Fact]
    public void A_phone_that_switched_accounts_only_gets_its_current_accounts_messages()
    {
        // Driver A used this phone and their copy was never removed; driver B signed in on it later.
        var devices = new[] { Phone("drvA", "phone1", T0), Phone("drvB", "phone1", T0.AddMinutes(30)), Phone("mgr", "tok-mgr") };

        // The manager writes to driver A: the phone is driver B's now - nothing goes to it.
        Assert.Empty(ChatPushPolicy.RecipientDevices(FromStaff("drvA", "mgr"), Everyone, devices));
        // The manager writes to driver B: it reaches the phone, for driver B.
        Device only = Assert.Single(ChatPushPolicy.RecipientDevices(FromStaff("drvB", "mgr"), Everyone, devices));
        Assert.Equal(("drvB", "phone1"), (only.Uid, only.Token));
    }

    [Fact]
    public void A_token_also_under_the_sender_is_never_used()
    {
        // The manager's phone once had the assistant signed in (newer) - the token is under both.
        var devices = new[] { Phone("drvA", "tok-drvA"), Phone("mgr", "phone2", T0), Phone("asst1", "phone2", T0.AddMinutes(1)) };
        Assert.Equal(new[] { "tok-drvA" }, Tokens(ChatPushPolicy.RecipientDevices(FromStaff("drvA", "mgr"), Everyone, devices)));
    }

    [Fact]
    public void A_token_two_accounts_registered_at_the_same_moment_reaches_neither()
    {
        var devices = new[] { Phone("drvA", "same", T0), Phone("mgr", "same", T0) };
        Assert.Empty(ChatPushPolicy.RecipientDevices(FromStaff("drvA", "asst1", "Assistant Manager"), Everyone, devices));
    }

    [Fact]
    public void Users_with_another_role_or_no_device_are_skipped()
    {
        var users = new[] { Manager, Asst1, new User("mech", "Mechanic"), new User("norole", null), Asst2 };
        var devices = new[] { Phone("mech", "tok-mech"), Phone("norole", "tok-norole"), Phone("asst2", "tok-asst2"), Phone("asst2", " ") };
        Assert.Equal(new[] { "tok-asst2" }, Tokens(ChatPushPolicy.RecipientDevices(FromDriver("drvA"), users, devices)));
    }

    [Fact]
    public void An_assistant_manager_gets_drivers_messages_and_other_staffs_replies_but_not_their_own()
    {
        Assert.Contains("asst1", ChatPushPolicy.RecipientUids(FromDriver("drvB"), Everyone));
        Assert.Contains("asst1", ChatPushPolicy.RecipientUids(FromStaff("drvB", "mgr"), Everyone));
        Assert.DoesNotContain("asst1", ChatPushPolicy.RecipientUids(FromStaff("drvB", "asst1", "Assistant Manager"), Everyone));
    }

    [Fact]
    public void Payload_is_small_and_names_the_recipient()
    {
        var toDriver = ChatPushPolicy.Payload("drvA", "drvA", recipientIsStaff: false, "Mara (Manager)", "Return by 6", "Dan Cruz");
        Assert.Equal(new[] { "body", "conversationId", "recipientUid", "title", "type" }, toDriver.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("drvA", toDriver[PushTokenRules.RecipientUidKey]);
        Assert.Equal(ChatPushPolicy.PushType, toDriver["type"]);

        var toStaff = ChatPushPolicy.Payload("drvA", "asst1", recipientIsStaff: true, "Dan Cruz", "On my way", " Dan Cruz ");
        Assert.Equal("asst1", toStaff[PushTokenRules.RecipientUidKey]);
        Assert.Equal("Dan Cruz", toStaff[ChatPushPolicy.DriverNameKey]);
    }

    [Fact]
    public void A_staff_message_with_no_sender_reaches_only_the_driver()
    {
        Assert.Equal(new[] { "drvA" }, ChatPushPolicy.RecipientUids(new Message("drvA", false, null, "Manager"), Everyone));
    }

    [Fact]
    public void Old_messages_are_not_pushed_after_a_deploy()
    {
        Assert.False(ChatPushPolicy.IsTooOldToPush(T0.AddMinutes(-1), T0));
        Assert.False(ChatPushPolicy.IsTooOldToPush(T0 - ChatPushPolicy.MaxMessageAge, T0));
        Assert.True(ChatPushPolicy.IsTooOldToPush(T0.AddMinutes(-6), T0));
        Assert.True(ChatPushPolicy.IsTooOldToPush(T0.AddDays(-3), T0));
        Assert.False(ChatPushPolicy.IsTooOldToPush(null, T0));
    }

    [Fact]
    public void Title_is_the_driver_name_or_the_staff_label()
    {
        Assert.Equal("Dan Cruz", ChatPushPolicy.Title(true, " Dan Cruz ", null, null));
        Assert.Equal("Driver", ChatPushPolicy.Title(true, "", null, null));
        Assert.Equal("Ana Reyes (Assistant Manager)", ChatPushPolicy.Title(false, "Dan Cruz", "Ana Reyes", "Assistant Manager"));
        Assert.Equal("Manager", ChatPushPolicy.Title(false, "Dan Cruz", null, null));
    }

    [Fact]
    public void Body_is_flattened_and_cut_at_a_word()
    {
        Assert.Equal("On my way, 5 mins", ChatPushPolicy.Body("On my way,\n  5 mins"));
        Assert.Equal(string.Empty, ChatPushPolicy.Body(null));

        string longText = string.Join(' ', Enumerable.Repeat("word", 60));
        string body = ChatPushPolicy.Body(longText);
        Assert.True(body.Length <= ChatPushPolicy.MaxBodyLength);
        Assert.EndsWith("word…", body);
    }

    [Fact]
    public void Body_never_splits_an_emoji()
    {
        string text = new string('a', ChatPushPolicy.MaxBodyLength - 2) + "😀😀😀";
        string body = ChatPushPolicy.Body(text);
        Assert.False(char.IsHighSurrogate(body[^2]));
        Assert.EndsWith("…", body);
    }
}
