using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.Packets;

namespace RimMult.ClientCore;

public enum ChatInputProblem
{
    None,

    /// <summary>"/w Name …" names nobody here.</summary>
    NoSuchPlayer,

    /// <summary>"/r …" but nobody has whispered yet.</summary>
    NoReply,
}

/// <summary>A typed chat line, understood: who hears it and what it says.</summary>
public sealed class ChatInputResult
{
    public ChatScope Scope { get; set; }
    public int TargetId { get; set; } = -1;
    public string Text { get; set; } = "";
    public ChatInputProblem Problem { get; set; }

    /// <summary>For <see cref="ChatInputProblem.NoSuchPlayer"/>: the name as typed.</summary>
    public string Name { get; set; } = "";
}

/// <summary>
/// Chat commands typed in the input line: "/a text" to allies, "/w Name text" whispers, "/r text" answers the last
/// whisper, "/all text" to everyone. The same keys on a Russian layout work too ("/ф", "/ц", "/к"). Anything else
/// starting with "/" goes to the server as a command.
/// </summary>
public static class ChatInput
{
    private static readonly string[] AlliesPrefixes = { "/a ", "/ф " };
    private static readonly string[] WhisperPrefixes = { "/w ", "/ц " };
    private static readonly string[] ReplyPrefixes = { "/r ", "/к " };
    private static readonly string[] AllPrefixes = { "/all ", "/все " };

    /// <param name="defaultScope">Where a line without a command goes (the chat's current channel).</param>
    /// <param name="lastWhisper">The other player of the last whisper, or -1.</param>
    public static ChatInputResult Parse(string input, ChatScope defaultScope, IReadOnlyList<PlayerInfo> players, int myId, int lastWhisper)
    {
        var text = input.Trim();
        if (Strip(text, AllPrefixes) is { } all)
            return new ChatInputResult { Scope = ChatScope.All, Text = all };
        if (Strip(text, AlliesPrefixes) is { } allies)
            return new ChatInputResult { Scope = ChatScope.Allies, Text = allies };
        if (Strip(text, ReplyPrefixes) is { } reply)
        {
            return lastWhisper >= 0 && players.Any(p => p.Id == lastWhisper)
                ? new ChatInputResult { Scope = ChatScope.Whisper, TargetId = lastWhisper, Text = reply }
                : new ChatInputResult { Problem = ChatInputProblem.NoReply };
        }
        if (Strip(text, WhisperPrefixes) is { } whisper)
        {
            // Names may have spaces: the longest name the line starts with wins.
            var target = players
                .Where(p => p.Id != myId && p.Name.Length > 0
                            && whisper.StartsWith(p.Name, StringComparison.OrdinalIgnoreCase)
                            && (whisper.Length == p.Name.Length || whisper[p.Name.Length] == ' '))
                .OrderByDescending(p => p.Name.Length)
                .FirstOrDefault();
            if (target == null)
            {
                var space = whisper.IndexOf(' ');
                return new ChatInputResult { Problem = ChatInputProblem.NoSuchPlayer, Name = space > 0 ? whisper.Substring(0, space) : whisper };
            }
            return new ChatInputResult { Scope = ChatScope.Whisper, TargetId = target.Id, Text = whisper.Substring(target.Name.Length).Trim() };
        }
        // Server commands always go as they are.
        var scope = text.StartsWith("/", StringComparison.Ordinal) ? ChatScope.All : defaultScope;
        return new ChatInputResult { Scope = scope, Text = text };
    }

    private static string? Strip(string text, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return text.Substring(prefix.Length).Trim();
        }
        return null;
    }
}
