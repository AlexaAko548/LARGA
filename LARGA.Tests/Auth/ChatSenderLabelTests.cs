using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Services;

namespace LARGA.Tests.Auth;

/// <summary>The shared driver conversation: who on the staff side wrote each message (ChatMessage.SenderLabel).</summary>
public class ChatSenderLabelTests
{
    [Theory]
    [InlineData("Ana Reyes", "Assistant Manager", "Ana Reyes (Assistant Manager)")]
    [InlineData("Mara Cruz", "Manager", "Mara Cruz (Manager)")]
    [InlineData(" Mara Cruz ", "", "Mara Cruz")]
    [InlineData("", "Assistant Manager", "Assistant Manager")]
    [InlineData(null, null, "Manager")] // sent before messages carried their sender
    public void StaffMessage_ShowsNameAndRole(string? name, string? role, string expected) =>
        Assert.Equal(expected, ChatMessage.LabelFor(isDriver: false, name, role));

    [Fact]
    public void AutomaticNotice_IsLabelledAsTheSystem()
    {
        ChatSenderInfo system = ChatSenderInfo.System;
        Assert.Equal("BLM Taxi (automatic)", ChatMessage.LabelFor(false, system.Name, system.Role));
        Assert.Equal("BLM Taxi (automatic)", ChatMessage.LabelFor(false, "anything", "system"));
    }

    [Fact]
    public void DriverMessage_HasNoLabel()
    {
        var message = new ChatMessage { IsDriver = true, SenderName = "Ana Reyes", SenderRole = "Manager" };
        Assert.Equal(string.Empty, message.SenderLabel);
        Assert.False(message.ShowSender);
    }

    // Shared conversation layout: the viewer's own messages on the right, everyone else's on the left.
    private static ChatMessage Staff(string? senderId) => new() { IsDriver = false, SenderId = senderId, SenderName = "x", SenderRole = "Manager" };
    private static readonly ChatMessage FromDriver = new() { IsDriver = true };

    [Fact]
    public void StaffViewer_OnlyTheirOwnMessagesAreMine()
    {
        Assert.True(Staff("asst1").IsMineFor("asst1", viewerIsDriver: false));
        Assert.False(Staff("mgr1").IsMineFor("asst1", viewerIsDriver: false));   // the Manager's, seen by an AM: left
        Assert.False(FromDriver.IsMineFor("asst1", viewerIsDriver: false));
        Assert.False(Staff(null).IsMineFor("asst1", viewerIsDriver: false));      // older, unsigned: left
        Assert.False(Staff("asst1").IsMineFor(null, viewerIsDriver: false));
    }

    [Fact]
    public void DriverViewer_TheirOwnMessagesAreMine()
    {
        Assert.True(FromDriver.IsMineFor(null, viewerIsDriver: true));
        Assert.False(Staff("mgr1").IsMineFor(null, viewerIsDriver: true));
    }

    [Fact]
    public void OtherStaffMessage_IsLabelledAndColouredApart()
    {
        ChatMessage colleague = Staff("mgr1");
        colleague.IsMine = colleague.IsMineFor("asst1", viewerIsDriver: false);
        Assert.True(colleague.IsOtherStaff);
        Assert.True(colleague.ShowSender);

        ChatMessage mine = Staff("asst1");
        mine.IsMine = mine.IsMineFor("asst1", viewerIsDriver: false);
        Assert.False(mine.IsOtherStaff);
        Assert.False(mine.ShowSender);
    }
}
