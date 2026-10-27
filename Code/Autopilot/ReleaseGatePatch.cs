using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace SpireCodex.Autopilot;

// The game hides AutoSlay and its debug seams behind NGame.IsReleaseGame(). While the autopilot
// is on the game answers false, which unlocks the same paths the developers drive their own
// AutoSlay with. Off, nothing changes.
[HarmonyPatch(typeof(NGame), nameof(NGame.IsReleaseGame))]
internal static class ReleaseGatePatch
{
    private static bool Prefix(ref bool __result)
    {
        if (!Autopilot.Enabled) return true;
        __result = false;
        return false;
    }
}
