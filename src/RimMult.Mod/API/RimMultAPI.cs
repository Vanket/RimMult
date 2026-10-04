using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.Coop;
using RimMult.Shared.Coop;
using Verse;

namespace RimMult.API;

/// <summary>
/// RimMult for other mods. Most mods need nothing: their things, pawns, comps and components travel with the game's
/// own saving. This is for the rest — a mod whose button changes state kept outside things, or opens its own window.
/// <para>
/// Use it without a hard dependency through reflection, or reference RimMult.dll (with <c>loadAfter</c>
/// vanket.rimmult) and check <see cref="IsAvailable"/>. All calls are main-thread only.
/// </para>
/// </summary>
public static class RimMultAPI
{
    /// <summary>Bumped when something here changes in a way callers must know about.</summary>
    public const int Version = 1;

    /// <summary>Always true when this type can be called (RimMult is loaded).</summary>
    public static bool IsAvailable => true;

    /// <summary>Connected to a RimMult server.</summary>
    public static bool IsMultiplayer => Multiplayer.IsActive;

    /// <summary>The server plays co-op (everyone in the host's colony) rather than separate colonies.</summary>
    public static bool IsCoop => Multiplayer.Session?.Mode == GameMode.Coop;

    /// <summary>This game hosts the co-op colony: it simulates, guests see it.</summary>
    public static bool IsCoopHost => IsCoop && Multiplayer.IsHosting;

    /// <summary>
    /// This game shows another game's copy (a co-op guest, or a player visiting another's game for a live raid or to
    /// help): it never ticks, and changes made here are lost unless sent with <see cref="SendCommand"/>.
    /// </summary>
    public static bool IsShowingCopy => CoopGuest.CopyInPlay;

    /// <summary>The game on this machine really simulates (everything but a copy).</summary>
    public static bool IsSimulating => !CoopGuest.CopyInPlay;

    /// <summary>This player's name on the server (or null when not connected).</summary>
    public static string? PlayerName => Multiplayer.Session is { } session ? session.NameOf(session.PlayerId) : null;

    /// <summary>Everyone on the server.</summary>
    public static IReadOnlyList<string> PlayerNames => Multiplayer.Session?.Players.Select(p => p.Name).ToList() ?? new List<string>();

    /// <summary>
    /// A command of this mod, run where the game simulates. Register it on every machine (in your mod's constructor
    /// or a static constructor); <paramref name="onHost"/> gets the bytes given to <see cref="SendCommand"/>.
    /// Use ids like "author.mod:action".
    /// </summary>
    public static void RegisterCommand(string id, Action<byte[]> onHost)
    {
        if (string.IsNullOrEmpty(id) || onHost == null)
            throw new ArgumentException("A command needs an id and a handler.");
        ModSync.RegisterCommand(id, onHost);
    }

    /// <summary>
    /// Runs a command registered with <see cref="RegisterCommand"/>: on a co-op guest it is sent to the host,
    /// anywhere else it runs right away. Typical use: in your button's action, instead of changing the state.
    /// </summary>
    public static void SendCommand(string id, byte[]? data = null) => ModSync.SendCommand(id, data ?? Array.Empty<byte>());

    /// <summary>
    /// State of this mod that guests should see, kept outside things and components (a static field, a cache). The
    /// host calls <paramref name="save"/> about once a second and, when the text changed, guests get it in
    /// <paramref name="load"/>. Game, world and map components of mods are already streamed without this.
    /// </summary>
    public static void RegisterState(string key, Func<string> save, Action<string> load)
    {
        if (string.IsNullOrEmpty(key) || save == null || load == null)
            throw new ArgumentException("A state needs a key, a save and a load.");
        ModSync.RegisterState(key, save, load);
    }

    /// <summary>
    /// Buttons of this type run on the guest's own copy instead of being pressed on the host (they only open this
    /// mod's window; what the window changes should go through <see cref="SendCommand"/>).
    /// </summary>
    public static void RegisterLocalGizmo(Type commandType) => ModSync.RegisterLocalGizmo(commandType);

    /// <summary>A component of this mod that must not be streamed to co-op guests (too big, or rebuilt locally).</summary>
    public static void ExcludeComponent(Type componentType) => ModSync.Exclude(componentType);
}
