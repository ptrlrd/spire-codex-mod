using System;
using System.Reflection;

namespace SpireCodex.Core;

// Identifies the local player so the damage tracker and the snapshot attribute to "me", not the
// whole co-op team. STS2 co-op is one shared combat run in lockstep, so the damage hooks fire on
// this client for BOTH players; filtering by the local player makes the DPS meter and the
// flattened snapshot per-player. Uses the game's static LocalContext.NetId (a ulong?, null in
// single-player) against Player.NetId (reached from a Creature via Creature.Player).
internal static class LocalPlayer
{
    private static bool _resolved;
    private static PropertyInfo? _netIdProp;

    // LocalContext.NetId: the local player's net id; null in single-player / before a lobby.
    private static ulong? LocalNetId()
    {
        if (!_resolved)
        {
            _resolved = true;
            _netIdProp = FindType("MegaCrit.Sts2.Core.Context.LocalContext")
                ?.GetProperty("NetId", BindingFlags.Public | BindingFlags.Static);
        }
        try { return _netIdProp?.GetValue(null) as ulong?; }
        catch { return null; }
    }

    // The local player's net id (LocalContext.NetId), for matching against run-history entries'
    // PlayerId in co-op. Null in single-player / before a lobby (where callers fall back to the
    // sole entry).
    public static ulong? NetId => LocalNetId();

    // Whether this is a co-op run (2+ players). Set by the producer each tick from the run's
    // player count, NOT from the net id (which RunManager sets to NetService.NetId at run start,
    // so it isn't reliably null in single-player). Player count is unambiguous: the damage tracker
    // only filters to the local player when this is true, so single-player stays unfiltered (and
    // poison/DoT keeps counting, exactly as before).
    private static volatile bool _coop;
    public static void SetCoop(bool coop) => _coop = coop;
    public static bool IsCoop => _coop;

    // Is this Player the local one? In single-player (no local net id) the only player reads as
    // local, so the snapshot flattens the right player in both modes. Null reads false.
    public static bool IsLocalPlayer(object? player)
    {
        if (player == null) return false;
        var local = LocalNetId();
        if (local == null) return true; // single-player: the only player is local
        var netId = Reflect.GetMember(player, "NetId");
        try { return netId != null && Convert.ToUInt64(netId) == local.Value; }
        catch { return false; }
    }

    // Is this Creature controlled by the local player? Monsters and pets (Creature.Player == null)
    // read false. Callers in the damage path guard with IsCoop, so this only matters in co-op.
    public static bool IsLocalCreature(object? creature)
        => IsLocalPlayer(Reflect.GetMember(creature, "Player"));

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { if (asm.GetType(fullName) is { } t) return t; }
            catch { /* dynamic assemblies can throw */ }
        }
        return null;
    }
}
