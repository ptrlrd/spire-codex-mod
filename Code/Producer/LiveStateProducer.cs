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
        RewardContext.HpPct = snapshot.InRun && snapshot.MaxHp > 0
            ? snapshot.CurrentHp * 100.0 / snapshot.MaxHp
            : null;
        // Keep the score cache on the run's character so plates/best-pick use the
        // character-adjusted numbers (no-op unless the character changed).
        Api.CodexScores.EnsureCharacter(snapshot.InRun ? snapshot.Character : null);
        SnapshotWriter.Write(snapshot);
    }
}
