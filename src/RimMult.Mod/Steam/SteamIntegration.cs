using System.Collections.Generic;
using Steamworks;
using UnityEngine;
using Verse.Steam;

namespace RimMult.Steam;

internal sealed class FriendHost
{
    public FriendHost(ulong steamId, string name)
    {
        SteamId = steamId;
        Name = name;
    }

    public ulong SteamId { get; }
    public string Name { get; }
}

/// <summary>
/// Identity and friends-list integration. A hosting player advertises itself through Steam rich presence
/// ("connect" key), which makes "Join game" work from the Steam friends list and lets the in-game list find hosts.
/// </summary>
internal static class SteamIntegration
{
    private const string ConnectKey = "connect";
    private const string ConnectPrefix = "rimmult:";
    private const float FriendsRefreshSeconds = 3f;

    private static Callback<GameRichPresenceJoinRequested_t>? _joinRequested;
    private static List<FriendHost> _friendHosts = new();
    private static float _friendsRefreshedAt = float.NegativeInfinity;

    public static bool Available => SteamManager.Initialized;

    public static ulong MySteamId => SteamUser.GetSteamID().m_SteamID;

    public static string MyName => SteamFriends.GetPersonaName();

    public static void Init()
    {
        if (!Available || _joinRequested != null)
            return;

        // "Join game" clicked in the Steam friends list while RimWorld is running.
        _joinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(request =>
        {
            if (TryParseConnect(request.m_rgchConnect, out var hostId))
                Multiplayer.JoinSteam(hostId, password: null);
        });
    }

    public static void SetHosting(bool hosting)
    {
        if (Available)
            SteamFriends.SetRichPresence(ConnectKey, hosting ? ConnectPrefix + MySteamId : "");
    }

    /// <summary>Friends currently hosting a RimMult game. Cached for a few seconds; cheap to call every frame.</summary>
    public static IReadOnlyList<FriendHost> FriendHosts()
    {
        if (!Available)
            return _friendHosts;

        var now = Time.realtimeSinceStartup;
        if (now - _friendsRefreshedAt < FriendsRefreshSeconds)
            return _friendHosts;
        _friendsRefreshedAt = now;

        var hosts = new List<FriendHost>();
        var appId = SteamUtils.GetAppID();
        var count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
        for (var i = 0; i < count; i++)
        {
            var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
            if (!SteamFriends.GetFriendGamePlayed(friend, out var game) || game.m_gameID.AppID() != appId)
                continue;
            if (TryParseConnect(SteamFriends.GetFriendRichPresence(friend, ConnectKey), out var hostId))
                hosts.Add(new FriendHost(hostId, SteamFriends.GetFriendPersonaName(friend)));
        }

        _friendHosts = hosts;
        return _friendHosts;
    }

    private static bool TryParseConnect(string? connect, out ulong hostId)
    {
        hostId = 0;
        return connect != null
               && connect.StartsWith(ConnectPrefix)
               && ulong.TryParse(connect.Substring(ConnectPrefix.Length), out hostId)
               && hostId != 0;
    }

    public static void OpenWorkshopPage(ulong workshopId) =>
        Application.OpenURL("steam://url/CommunityFilePage/" + workshopId);
}
