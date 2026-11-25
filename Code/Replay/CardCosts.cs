using System.Collections.Generic;

namespace SpireCodex.Replay;

internal static class CardCosts
{
    private static readonly Dictionary<int, int> Shown = new();

    public static void Reset()
    {
        lock (Shown) Shown.Clear();
    }

    public static bool Changed(int instance, int baseCost, int cost)
    {
        if (instance <= 0) return false;
        lock (Shown)
        {
            var last = Shown.TryGetValue(instance, out var seen) ? seen : baseCost;
            if (last == cost) return false;
            Shown[instance] = cost;
            return true;
        }
    }
}
