using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using RimMult.Shared.Net;
using Steamworks;

namespace RimMult.Steam;

/// <summary>
/// Keeps Steam connections alive while the game's main thread is stuck. Generating a planet or a map with a few
/// hundred mods freezes the game for minutes; our networking runs on that thread, so the other side heard nothing and
/// dropped the connection after <see cref="SteamP2P.Timeout"/>. While the main thread is stalled, a background thread
/// sends heartbeats to every peer (nothing is read or decided there: the main thread catches up on the rest).
/// UDP connections need none of this: LiteNetLib keeps them alive on its own thread.
/// </summary>
internal static class SteamKeepAlive
{
    /// <summary>The main thread counts as stuck after this long without a frame.</summary>
    private const long StallMs = 2000;

    private const int CheckEveryMs = 1000;

    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, int> Peers = new();
    private static readonly byte[] Heartbeat = P2PFrame.Control(P2PFrameKind.Heartbeat);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long _lastFrameMs;
    private static Thread? _thread;

    /// <summary>Called every frame from the main thread.</summary>
    public static void MainThreadAlive() => Interlocked.Exchange(ref _lastFrameMs, Clock.ElapsedMilliseconds);

    /// <summary>A connection to this peer is open (counted: the host and a client of the same player may share one).</summary>
    public static void Add(CSteamID peer)
    {
        lock (Gate)
        {
            Peers.TryGetValue(peer.m_SteamID, out var count);
            Peers[peer.m_SteamID] = count + 1;
            if (_thread == null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "RimMult Steam keep-alive" };
                _thread.Start();
            }
        }
    }

    public static void Remove(CSteamID peer)
    {
        lock (Gate)
        {
            if (!Peers.TryGetValue(peer.m_SteamID, out var count))
                return;
            if (count <= 1)
                Peers.Remove(peer.m_SteamID);
            else
                Peers[peer.m_SteamID] = count - 1;
        }
    }

    private static void Run()
    {
        var peers = new List<ulong>();
        while (true)
        {
            Thread.Sleep(CheckEveryMs);
            if (Clock.ElapsedMilliseconds - Interlocked.Read(ref _lastFrameMs) < StallMs)
                continue;
            peers.Clear();
            lock (Gate)
                peers.AddRange(Peers.Keys);
            foreach (var peer in peers)
            {
                try
                {
                    SteamNetworking.SendP2PPacket(new CSteamID(peer), Heartbeat, (uint)Heartbeat.Length, EP2PSend.k_EP2PSendUnreliable);
                }
                catch (Exception)
                {
                    // Steam shutting down, or the peer is gone: nothing to keep alive.
                }
            }
        }
    }
}
