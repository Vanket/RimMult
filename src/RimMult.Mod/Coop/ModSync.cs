using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimMult.Shared.Coop;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimMult.Coop;

/// <summary>
/// Other mods in co-op: their commands (sent by a guest, run by the host), their registered state, and — without
/// any work on their side — their game, world and map components, all streamed from the host to the guests when
/// they change. A component that fails to load on a guest is left alone from then on.
/// </summary>
internal static class ModSync
{
    private const float ComponentInterval = 2f;

    /// <summary>A component this big isn't streamed (it would crowd out everything else).</summary>
    private const int MaxComponentChars = 200_000;

    private sealed class State
    {
        public Func<string> Save = null!;
        public Action<string> Load = null!;
    }

    private static readonly Dictionary<string, Action<byte[]>> Commands = new();
    private static readonly Dictionary<string, State> States = new();
    private static readonly HashSet<Type> LocalGizmos = new();
    private static readonly HashSet<Type> Excluded = new();

    /// <summary>What was last sent per key (as a hash): only changes go out.</summary>
    private static readonly Dictionary<string, int> Sent = new();

    /// <summary>Keys that failed to save or load here: left alone from then on.</summary>
    private static readonly HashSet<string> Broken = new();

    private static float _lastComponents;

    private static readonly Assembly GameAssembly = typeof(Game).Assembly;
    private static readonly Assembly OwnAssembly = typeof(ModSync).Assembly;

    // ---------- registration (RimMultAPI) ----------

    public static void RegisterCommand(string id, Action<byte[]> onHost) => Commands[id] = onHost;

    public static void RegisterState(string key, Func<string> save, Action<string> load) =>
        States["api:" + key] = new State { Save = save, Load = load };

    public static void RegisterLocalGizmo(Type commandType) => LocalGizmos.Add(commandType);

    public static void Exclude(Type componentType) => Excluded.Add(componentType);

    public static bool IsLocalGizmo(Type type)
    {
        foreach (var local in LocalGizmos)
            if (local.IsAssignableFrom(type))
                return true;
        return false;
    }

    /// <summary>A guest's mod command reaches the host (or runs right away where this game simulates).</summary>
    public static void SendCommand(string id, byte[] data)
    {
        if (CoopGuest.Active && !CoopGuest.Visiting)
        {
            CoopCommands.Send(new CoopCommand { Kind = CoopCommandKind.Mod, Name = id, Extra = Convert.ToBase64String(data) });
            return;
        }
        RunCommand(id, data);
    }

    public static void RunCommand(string id, byte[] data)
    {
        if (!Commands.TryGetValue(id, out var handler))
        {
            Log.WarningOnce($"[RimMult] No mod command '{id}' here (is the mod installed?).", id.GetHashCode());
            return;
        }
        try
        {
            handler(data);
        }
        catch (Exception e)
        {
            Log.Error($"[RimMult] Mod command '{id}' failed: {e}");
        }
    }

    // ---------- host: what changed ----------

    /// <summary>A guest just loaded the game: it has every state as it is now.</summary>
    public static void Forget()
    {
        Sent.Clear();
        _lastComponents = float.NegativeInfinity;
    }

    public static void Build(CoopWorld world)
    {
        foreach (var pair in States)
            Offer(world, pair.Key, () => pair.Value.Save());

        var now = Time.realtimeSinceStartup;
        if (!RimMultMod.Instance.Settings.SyncModComponents || now - _lastComponents < ComponentInterval)
            return;
        _lastComponents = now;
        foreach (var (key, component) in Components())
            Offer(world, key, () => ScribeMemory.Save(component.ExposeData));
    }

    private static void Offer(CoopWorld world, string key, Func<string> save)
    {
        if (Broken.Contains(key))
            return;
        string data;
        try
        {
            data = save() ?? "";
        }
        catch (Exception e)
        {
            Broken.Add(key);
            Log.Warning($"[RimMult] Co-op: {key} can't be shared with guests: {e.Message}");
            return;
        }
        if (data.Length > MaxComponentChars)
        {
            Broken.Add(key);
            Log.Message($"[RimMult] Co-op: {key} is too big to stream ({data.Length} chars); guests see it as of joining.");
            return;
        }
        var hash = data.GetHashCode() ^ data.Length;
        if (Sent.TryGetValue(key, out var old) && old == hash)
            return;
        var first = !Sent.ContainsKey(key);
        Sent[key] = hash;
        // The first look at a component only notes where it stands (the guest got it with the game);
        // a registered state goes out right away.
        if (!first || key.StartsWith("api:", StringComparison.Ordinal))
            world.ModStates.Add((key, data));
    }

    /// <summary>Mods' game, world and map components (not the game's own, not RimMult's), with their keys.</summary>
    private static IEnumerable<(string Key, IExposable Component)> Components()
    {
        foreach (var component in Current.Game.components)
            if (FromMod(component))
                yield return ("gc:" + component.GetType().FullName, component);
        if (Find.World != null)
            foreach (var component in Find.World.components)
                if (FromMod(component))
                    yield return ("wc:" + component.GetType().FullName, component);
        foreach (var map in Find.Maps)
            foreach (var component in map.components)
                if (FromMod(component))
                    yield return ($"mc:{map.uniqueID}:{component.GetType().FullName}", component);
    }

    private static bool FromMod(object component)
    {
        var type = component.GetType();
        return type.Assembly != GameAssembly && type.Assembly != OwnAssembly && !Excluded.Contains(type);
    }

    // ---------- guest: taking it over ----------

    public static void Apply(List<(string Key, string Data)> states)
    {
        foreach (var (key, data) in states)
        {
            if (Broken.Contains(key))
                continue;
            try
            {
                if (States.TryGetValue(key, out var state))
                {
                    state.Load(data);
                    continue;
                }
                if (FindComponent(key) is { } component)
                    ScribeMemory.Load(ScribeMemory.Parse(data), new HashSet<string>(), component.ExposeData);
            }
            catch (Exception e)
            {
                Broken.Add(key);
                Log.Warning($"[RimMult] Co-op: could not take over {key} from the host (left as it was): {e.Message}");
            }
        }
    }

    private static IExposable? FindComponent(string key)
    {
        if (key.StartsWith("gc:", StringComparison.Ordinal))
            return Current.Game.components.FirstOrDefault(c => c.GetType().FullName == key.Substring(3));
        if (key.StartsWith("wc:", StringComparison.Ordinal))
            return Find.World?.components.FirstOrDefault(c => c.GetType().FullName == key.Substring(3));
        if (key.StartsWith("mc:", StringComparison.Ordinal))
        {
            var rest = key.Substring(3);
            var colon = rest.IndexOf(':');
            if (colon > 0 && int.TryParse(rest.Substring(0, colon), out var mapId)
                          && Find.Maps.FirstOrDefault(m => m.uniqueID == mapId) is { } map)
                return map.components.FirstOrDefault(c => c.GetType().FullName == rest.Substring(colon + 1));
        }
        return null;
    }
}
