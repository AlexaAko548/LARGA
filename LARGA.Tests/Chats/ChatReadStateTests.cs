using LARGA.SharedCore.Chats;
using Conversation = LARGA.SharedCore.Chats.ChatReadState.Conversation;

namespace LARGA.Tests.Chats;

/// <summary>Per-person unread state of a driver conversation and the badge numbers (ChatReadState).</summary>
public class ChatReadStateTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 2, 0, 0, DateTimeKind.Utc);

    private static Conversation Chat(string driverId = "drvA", DateTime? lastMessageAt = null, string? lastSender = null,
        bool staffIsUnread = false, params (string Uid, DateTime At)[] reads) =>
        new(driverId, lastMessageAt, lastSender, reads.ToDictionary(r => r.Uid, r => r.At), staffIsUnread);

    // ---- Who has it unread ---------------------------------------------------

    [Fact]
    public void Driver_message_is_unread_for_every_staff_member_until_each_one_reads_it()
    {
        Conversation chat = Chat(lastMessageAt: T0, lastSender: "drvA", staffIsUnread: false, reads: ("mgr", T0));

        Assert.False(chat.IsUnreadFor("mgr", viewerIsStaff: true));   // read it
        Assert.True(chat.IsUnreadFor("asst1", viewerIsStaff: true));  // hasn't: per person, not shared
        Assert.False(chat.IsUnreadFor("drvA", viewerIsStaff: false)); // their own message
    }

    [Fact]
    public void Staff_message_is_unread_for_the_driver_and_the_other_staff_not_the_sender()
    {
        Conversation chat = Chat(lastMessageAt: T0, lastSender: "asst1", reads: new[] { ("mgr", T0.AddMinutes(-5)), ("drvA", T0.AddMinutes(-5)) });

        Assert.True(chat.IsUnreadFor("drvA", viewerIsStaff: false));
        Assert.True(chat.IsUnreadFor("mgr", viewerIsStaff: true));
        Assert.True(chat.IsUnreadFor("asst2", viewerIsStaff: true));
        Assert.False(chat.IsUnreadFor("asst1", viewerIsStaff: true));
    }

    [Fact]
    public void System_notices_are_unread_for_the_driver_and_staff()
    {
        Conversation chat = Chat(lastMessageAt: T0, lastSender: ChatReadState.SystemSenderId);

        Assert.True(chat.IsUnreadFor("drvA", viewerIsStaff: false));
        Assert.True(chat.IsUnreadFor("mgr", viewerIsStaff: true));
    }

    [Fact]
    public void Reading_up_to_the_newest_message_clears_it_and_a_newer_one_brings_it_back()
    {
        Conversation read = Chat(lastMessageAt: T0, lastSender: "mgr", reads: ("drvA", T0));
        Assert.False(read.IsUnreadFor("drvA", viewerIsStaff: false));

        Conversation newer = read with { LastMessageAtUtc = T0.AddSeconds(1) };
        Assert.True(newer.IsUnreadFor("drvA", viewerIsStaff: false));
    }

    [Fact]
    public void Times_are_compared_to_the_millisecond_the_phone_reads_them_at()
    {
        // Server time has microseconds; the phone reads (and copies into the marker) milliseconds.
        Conversation chat = Chat(lastMessageAt: T0.AddTicks(4567), lastSender: "mgr", reads: ("drvA", T0));
        Assert.False(chat.IsUnreadFor("drvA", viewerIsStaff: false));
    }

    [Fact]
    public void The_shared_isUnread_flag_still_counts_for_staff_but_never_for_the_driver()
    {
        // A driver message from an older app build (no lastMessageAt) that no staff member opened yet.
        Conversation chat = Chat(staffIsUnread: true);

        Assert.True(chat.IsUnreadFor("mgr", viewerIsStaff: true));
        Assert.False(chat.IsUnreadFor("drvA", viewerIsStaff: false));
    }

    [Fact]
    public void Rollout_old_history_shows_no_badge()
    {
        // Conversations from before this release: no lastMessageAt and no markers.
        Assert.False(Chat(staffIsUnread: false).IsUnreadFor("mgr", viewerIsStaff: true));
        Assert.False(Chat(staffIsUnread: false).IsUnreadFor("drvA", viewerIsStaff: false));
        // ...but a staff member who hasn't opened a conversation since a new message has it unread.
        Assert.True(Chat(lastMessageAt: T0, lastSender: "asst1").IsUnreadFor("mgr", viewerIsStaff: true));
    }

    [Fact]
    public void No_signed_in_user_has_nothing_unread()
    {
        Assert.False(Chat(lastMessageAt: T0, lastSender: "mgr", staffIsUnread: true).IsUnreadFor(null, viewerIsStaff: true));
    }

    // ---- Badge numbers -------------------------------------------------------

    [Fact]
    public void Chats_badge_counts_conversations_not_messages()
    {
        var conversations = new[]
        {
            Chat("drvA", T0, "drvA"),                          // unread
            Chat("drvB", T0, "drvB", staffIsUnread: true),     // unread
            Chat("drvC", T0, "drvC", reads: ("mgr", T0)),      // read by this manager
            Chat("drvD", T0, "mgr"),                           // their own reply
            Chat("drvE", null, null, staffIsUnread: false),    // old history
            Chat("drvF", T0.AddMinutes(1), "system", reads: ("mgr", T0)), // system notice after reading
        };

        Assert.Equal(3, ChatReadState.UnreadConversationCount(conversations, "mgr"));
        Assert.Equal(5, ChatReadState.UnreadConversationCount(conversations, "asst1")); // every one but the old one
    }

    [Fact]
    public void Driver_badge_is_one_while_their_conversation_has_unread_messages()
    {
        Assert.Equal(0, ChatReadState.DriverBadgeCount(null, "drvA"));
        Assert.Equal(1, ChatReadState.DriverBadgeCount(Chat(lastMessageAt: T0, lastSender: "mgr"), "drvA"));
        Assert.Equal(0, ChatReadState.DriverBadgeCount(Chat(lastMessageAt: T0, lastSender: "mgr", reads: ("drvA", T0)), "drvA"));
        Assert.Equal(0, ChatReadState.DriverBadgeCount(Chat(lastMessageAt: T0, lastSender: "drvA"), "drvA"));
        // The shared staff flag is about staff, never the driver.
        Assert.Equal(0, ChatReadState.DriverBadgeCount(Chat(staffIsUnread: true), "drvA"));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(1, "1")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(2500, "99+")]
    public void Badge_text_hides_at_zero_and_caps_at_99_plus(int count, string? text)
    {
        Assert.Equal(text, ChatReadState.BadgeText(count));
    }

    // ---- Marking read --------------------------------------------------------

    [Fact]
    public void Marking_read_copies_the_newest_message_time_into_the_readers_own_marker()
    {
        Assert.Equal(T0, Chat(lastMessageAt: T0, lastSender: "drvA").ReadMarkerToWrite("mgr"));
        Assert.Equal(T0, Chat(lastMessageAt: T0, lastSender: "drvA", reads: ("mgr", T0.AddMinutes(-1))).ReadMarkerToWrite("mgr"));
    }

    [Fact]
    public void Nothing_to_write_when_already_read_own_message_or_old_history()
    {
        Assert.Null(Chat(lastMessageAt: T0, lastSender: "drvA", reads: ("mgr", T0)).ReadMarkerToWrite("mgr"));
        Assert.Null(Chat(lastMessageAt: T0, lastSender: "mgr").ReadMarkerToWrite("mgr"));
        Assert.Null(Chat(staffIsUnread: true).ReadMarkerToWrite("mgr"));
        Assert.Null(Chat(lastMessageAt: T0, lastSender: "drvA").ReadMarkerToWrite(null));
    }

    [Fact]
    public void Markers_are_stored_as_unix_milliseconds_and_read_back_as_the_same_time()
    {
        // Stored as a number: Plugin.Firebase.Firestore 4.0.0 throws on a timestamp inside a map.
        DateTime lastMessage = T0.AddTicks(1234567); // server time, finer than a millisecond
        long stored = ChatReadState.ToUnixMillis(lastMessage);

        Assert.Equal(new DateTimeOffset(T0).ToUnixTimeMilliseconds() + 123, stored);
        DateTime readBack = ChatReadState.FromUnixMillis(stored)!.Value;
        Assert.Equal(DateTimeKind.Utc, readBack.Kind);

        Conversation chat = Chat(lastMessageAt: lastMessage, lastSender: "mgr", reads: ("drvA", readBack));
        Assert.False(chat.IsUnreadFor("drvA", viewerIsStaff: false));
    }

    [Theory]
    [InlineData(1762221600000L)]
    [InlineData(1762221600000.0)]
    public void Marker_numbers_come_back_whatever_numeric_type_the_plugin_hands_over(object value)
    {
        Assert.Equal(new DateTime(2025, 11, 4, 2, 0, 0, DateTimeKind.Utc), ChatReadState.FromUnixMillis(value));
    }

    [Fact]
    public void Anything_that_isnt_a_sane_marker_reads_as_no_marker()
    {
        Assert.Null(ChatReadState.FromUnixMillis(null));
        Assert.Null(ChatReadState.FromUnixMillis("1762221600000"));
        Assert.Null(ChatReadState.FromUnixMillis(-5L));
        Assert.Null(ChatReadState.FromUnixMillis(DateTimeOffset.UtcNow)); // an old timestamp-style value
        Assert.Null(ChatReadState.FromUnixMillis(double.NaN));
    }

    [Fact]
    public void After_writing_the_marker_the_conversation_reads_as_read()
    {
        Conversation chat = Chat(lastMessageAt: T0, lastSender: "drvA");
        DateTime marker = chat.ReadMarkerToWrite("asst1")!.Value;

        Conversation after = chat with { LastReadAtUtc = new Dictionary<string, DateTime> { ["asst1"] = marker } };

        Assert.False(after.IsUnreadFor("asst1", viewerIsStaff: true));
        Assert.True(after.IsUnreadFor("mgr", viewerIsStaff: true)); // only for the reader
    }
}
