using LARGA.SharedCore.Chats;
using User = LARGA.SharedCore.Chats.ChatPushPolicy.User;
using Message = LARGA.SharedCore.Chats.ChatPushPolicy.Message;

namespace LARGA.Tests.Chats;

/// <summary>Who ManagerWeb's ChatPushService pushes a new chat message to (ChatPushPolicy).</summary>
public class ChatPushPolicyTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 2, 0, 0, DateTimeKind.Utc);

    // Driver A and B, a Manager and two Assistant Managers (roles written the ways the apps accept).
    private static readonly User DriverA = new("drvA", "Driver", "tok-drvA", T0);
    private static readonly User DriverB = new("drvB", " driver ", "tok-drvB", T0);
    private static readonly User Manager = new("mgr", "Manager", "tok-mgr", T0);
    private static readonly User Asst1 = new("asst1", "Assistant Manager", "tok-asst1", T0);
    private static readonly User Asst2 = new("asst2", "assistant manager", "tok-asst2", T0);
    private static readonly User[] Everyone = { DriverA, DriverB, Manager, Asst1, Asst2 };

    private static Message FromDriver(string driverId) => new(driverId, true, null, null);
    private static Message FromStaff(string driverId, string senderId, string role = "Manager") => new(driverId, false, senderId, role);

    [Fact]
    public void Driver_message_goes_to_every_manager_and_assistant_manager()
    {
        Assert.Equal(new[] { "mgr", "asst1", "asst2" }, ChatPushPolicy.RecipientUids(FromDriver("drvA"), Everyone));
        Assert.Equal(new[] { "tok-mgr", "tok-asst1", "tok-asst2" }, ChatPushPolicy.RecipientTokens(FromDriver("drvA"), Everyone));
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
        Assert.DoesNotContain("tok-drvB", ChatPushPolicy.RecipientTokens(FromStaff("drvA", "mgr"), Everyone));
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
        Assert.Empty(ChatPushPolicy.RecipientTokens(notice, Everyone));
    }

    [Fact]
    public void Sender_is_not_notified_even_on_another_phone_with_the_same_token()
    {
        // The manager's token also sits (older) on asst2's doc: that phone is the manager's now.
        var users = new[] { DriverA, Manager, Asst1, Asst2 with { FcmToken = "tok-mgr", TokenUpdatedAtUtc = T0.AddHours(-1) } };

        IReadOnlyList<string> tokens = ChatPushPolicy.RecipientTokens(FromStaff("drvA", "mgr"), users);

        Assert.DoesNotContain("tok-mgr", tokens);
        Assert.Equal(new[] { "tok-drvA", "tok-asst1" }, tokens);
    }

    [Fact]
    public void A_phone_that_switched_accounts_only_gets_its_current_accounts_messages()
    {
        // Driver A used this phone and their token was never cleared; driver B signed in on it later.
        var users = new[]
        {
            DriverA with { FcmToken = "phone1", TokenUpdatedAtUtc = T0 },
            DriverB with { FcmToken = "phone1", TokenUpdatedAtUtc = T0.AddMinutes(30) },
            Manager, Asst1,
        };

        // The manager writes to driver A: driver A's stale copy of the token now belongs to driver B.
        Assert.DoesNotContain("phone1", ChatPushPolicy.RecipientTokens(FromStaff("drvA", "mgr"), users));
        // The manager writes to driver B: it reaches the phone.
        Assert.Contains("phone1", ChatPushPolicy.RecipientTokens(FromStaff("drvB", "mgr"), users));
    }

    [Fact]
    public void A_token_also_on_the_senders_doc_is_never_used()
    {
        // The manager's phone once had the assistant signed in (newer) - the token is on both docs.
        var users = new[] { DriverA, Manager with { FcmToken = "phone2", TokenUpdatedAtUtc = T0 }, Asst1 with { FcmToken = "phone2", TokenUpdatedAtUtc = T0.AddMinutes(1) } };
        Assert.Equal(new[] { "tok-drvA" }, ChatPushPolicy.RecipientTokens(FromStaff("drvA", "mgr"), users));
    }

    [Fact]
    public void A_token_two_accounts_saved_at_the_same_moment_reaches_neither()
    {
        var users = new[] { DriverA with { FcmToken = "same" }, Manager with { FcmToken = "same" }, Asst1 };
        Assert.Empty(ChatPushPolicy.RecipientTokens(FromStaff("drvA", "asst1", "Assistant Manager"), users));
    }

    [Fact]
    public void Users_without_a_token_or_with_another_role_are_skipped_and_tokens_are_distinct()
    {
        var users = new[]
        {
            Manager with { FcmToken = null },
            Asst1 with { FcmToken = " " },
            new User("mech", "Mechanic", "tok-mech", T0),
            new User("norole", null, "tok-norole", T0),
            Asst2,
        };
        Assert.Equal(new[] { "tok-asst2" }, ChatPushPolicy.RecipientTokens(FromDriver("drvA"), users));
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
