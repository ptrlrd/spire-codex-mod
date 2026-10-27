using System;

namespace SpireCodex.Autopilot;

// What the HUD shows: the engine's latest recommendation per decision kind, plus the status line.
internal static class Advice
{
    public static string Status = "off";
    public static string Map = "";
    public static string Combat = "";
    public static string Picks = "";
    public static DateTimeOffset Updated = DateTimeOffset.MinValue;

    public static void Set(ref string slot, string text)
    {
        slot = text;
        Updated = DateTimeOffset.UtcNow;
    }
}
