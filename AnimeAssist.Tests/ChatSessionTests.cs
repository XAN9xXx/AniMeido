using AniMeido.Plugin.Chat.Models;

namespace AniMeido.Tests;

public sealed class ChatSessionTests
{
    [Fact]
    public void TrySend_ValidText_AddsTrimmedOwnMessage()
    {
        var session = new ChatSession();
        var initialCount = session.CurrentRoom.Messages.Count;

        var sent = session.TrySend("  hello  ");

        Assert.True(sent);
        Assert.Equal(initialCount + 1, session.CurrentRoom.Messages.Count);
        var message = session.CurrentRoom.Messages[^1];
        Assert.Equal("hello", message.Content);
        Assert.True(message.IsOwnMessage);
    }

    [Fact]
    public void SelectRoom_SendMessage_ChangesOnlySelectedRoom()
    {
        var session = new ChatSession();
        var firstRoom = session.Rooms[0];
        var secondRoom = session.Rooms[1];
        var firstRoomCount = firstRoom.Messages.Count;
        var secondRoomCount = secondRoom.Messages.Count;

        session.SelectRoom(secondRoom);
        var sent = session.TrySend("second room");

        Assert.True(sent);
        Assert.Same(secondRoom, session.CurrentRoom);
        Assert.Equal(firstRoomCount, firstRoom.Messages.Count);
        Assert.Equal(secondRoomCount + 1, secondRoom.Messages.Count);
    }
}
