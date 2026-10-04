using RimMult.ClientCore;
using RimMult.Shared.Packets;

namespace RimMult.Tests;

public class ChatInputTests
{
    private static readonly List<PlayerInfo> Players =
    [
        new PlayerInfo { Id = 1, Name = "Vanket" },
        new PlayerInfo { Id = 2, Name = "Big Bob" },
        new PlayerInfo { Id = 3, Name = "Big" },
    ];

    private static ChatInputResult Parse(string text, ChatScope scope = ChatScope.All, int lastWhisper = -1) =>
        ChatInput.Parse(text, scope, Players, myId: 1, lastWhisper);

    [Fact]
    public void PlainLinesGoToTheCurrentChannel()
    {
        Assert.Equal((ChatScope.All, "hello"), (Parse(" hello ").Scope, Parse(" hello ").Text));
        Assert.Equal(ChatScope.Allies, Parse("hello", ChatScope.Allies).Scope);
        // Server commands are never whispered or kept among allies.
        Assert.Equal((ChatScope.All, "/players"), (Parse("/players", ChatScope.Allies).Scope, Parse("/players", ChatScope.Allies).Text));
        Assert.Equal((ChatScope.All, "to all"), (Parse("/all to all", ChatScope.Allies).Scope, Parse("/all to all", ChatScope.Allies).Text));
    }

    [Fact]
    public void AllyLinesOnEitherLayout()
    {
        Assert.Equal((ChatScope.Allies, "attack at dawn"), (Parse("/a attack at dawn").Scope, Parse("/a attack at dawn").Text));
        Assert.Equal((ChatScope.Allies, "в атаку"), (Parse("/ф в атаку").Scope, Parse("/ф в атаку").Text));
    }

    [Fact]
    public void WhispersFindTheLongestName()
    {
        var bob = Parse("/w big bob hi there");
        Assert.Equal((ChatScope.Whisper, 2, "hi there"), (bob.Scope, bob.TargetId, bob.Text));

        var big = Parse("/ц Big привет");
        Assert.Equal((ChatScope.Whisper, 3, "привет"), (big.Scope, big.TargetId, big.Text));

        // Not to oneself, and nobody by that name.
        Assert.Equal((ChatInputProblem.NoSuchPlayer, "Vanket"), (Parse("/w Vanket hi").Problem, Parse("/w Vanket hi").Name));
        Assert.Equal((ChatInputProblem.NoSuchPlayer, "Alice"), (Parse("/w Alice hi").Problem, Parse("/w Alice hi").Name));
    }

    [Fact]
    public void ReplyGoesToTheLastWhisper()
    {
        var reply = Parse("/r ok", lastWhisper: 2);
        Assert.Equal((ChatScope.Whisper, 2, "ok"), (reply.Scope, reply.TargetId, reply.Text));
        Assert.Equal(ChatInputProblem.NoReply, Parse("/к ok").Problem);
        Assert.Equal(ChatInputProblem.NoReply, Parse("/r ok", lastWhisper: 9).Problem); // gone meanwhile
    }
}
