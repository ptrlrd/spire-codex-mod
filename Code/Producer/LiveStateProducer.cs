using Godot;
using SpireCodex.Core;

namespace SpireCodex.Producer;

// A Node we attach to the scene tree so we get a per-frame tick. Every ~100ms it
// reads the live RunState and writes the snapshot file. Phase 0 proves this loop
// reads real run data; Phase 1 fills in deck/relic ids and wires run upload.
public partial class LiveStateProducer : Node
{
    private const double IntervalSeconds = 0.1; // 10 Hz
    private static LiveStateProducer? _instance;
    private double _accum;

    // Most recent snapshot, shared with the in-game overlay so it does not re-read.
    public static Snapshot? Latest { get; private set; }

    public static void Start()
    {
        if (_instance != null) return;
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            MainFile.Logger.Info("no SceneTree; producer not started");
            return;
        }

        _instance = new LiveStateProducer { Name = "SpireCodexProducer" };
        // Defer: we may be mid-initialization, where adding children directly is unsafe.
        tree.Root.CallDeferred(Node.MethodName.AddChild, _instance);
        MainFile.Logger.Info($"live-state producer started; writing {Config.LiveStatePath}");
    }

    public override void _Process(double delta)
    {
        _accum += delta;
        if (_accum < IntervalSeconds) return;
        _accum = 0;

        var snapshot = Sts2Access.ReadSnapshot();
        // Tell the companion overlay whether we're the one uploading runs,
        // so it doesn't also submit the same .run (overlay reads
        // `uploads_runs` from the live feed).
        snapshot.UploadsRuns = Config.UploadRuns;
        Latest = snapshot;
        RewardContext.Character = snapshot.InRun ? snapshot.Character : null;
        // HpPct feeds the campfire "At your HP" tip. A rest heal writes Creature.CurrentHp
        // instantly (synchronously on confirm) while the HP bar animates over ~2s, so a raw read
        // briefly runs ahead of the visible bar (e.g. 27/75 shows as 54/75 = 72% mid-heal). Don't
        // let HpPct rise while on the rest screen — hold the decision-relevant arrival HP that
        // matches the bar. It resumes tracking live HP the moment you leave the rest site.
        var hpPct = snapshot.InRun && snapshot.MaxHp > 0
            ? snapshot.CurrentHp * 100.0 / snapshot.MaxHp
            : (double?)null;
        if (snapshot.Screen == "rest" && hpPct is { } now && RewardContext.HpPct is { } prev && now > prev)
            hpPct = prev;
        RewardContext.HpPct = hpPct;
        // Keep the score cache on the run's character so plates/best-pick use the
        // character-adjusted numbers (no-op unless the character changed).
        Api.CodexScores.EnsureCharacter(snapshot.InRun ? snapshot.Character : null);
        // Apply the stat bracket chosen in settings (no-op unless it changed).
        Api.CodexScores.SetFilter(SpireCodexConfig.StatsFilterKey);
        SnapshotWriter.Write(snapshot);
    }
}
