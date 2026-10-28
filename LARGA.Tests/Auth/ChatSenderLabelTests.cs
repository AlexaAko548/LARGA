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
}
