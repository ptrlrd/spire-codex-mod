namespace SpireCodex.Core;

// Holds the run-death detail (killer name + loss-message quote) captured at the moment of death by
// RunEvents.DeathPrefix, while the combat encounter is still live. The snapshot reads it so the
// detail can ride the heartbeat and the final ended ping after the run flips to game-over (by which
// point the combat is gone). Reset per run by seed.
public static class DeathCapture
{
    private static string? _seed;

    public static Producer.DeathInfo? Latest { get; private set; }

    public static void Set(string? by, string? line) => Latest = new Producer.DeathInfo(by, line);

    // Clear when a new run (new seed) begins, so a prior death can't leak into the next run.
    public static void NoteRun(string? seed)
    {
        if (seed == _seed) return;
        _seed = seed;
        Latest = null;
    }
}
