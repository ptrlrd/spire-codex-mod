using Godot;
using SpireCodex.Core;

namespace SpireCodex.Producer;

public partial class LiveStateProducer : Node
{
    private const double IntervalSeconds = 0.1;
    private static LiveStateProducer? _instance;
    private double _accum;

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
        tree.Root.CallDeferred(Node.MethodName.AddChild, _instance);
        MainFile.Logger.Info($"live-state producer started; writing {Config.LiveStatePath}");
    }

    public override void _Process(double delta)
    {
        _accum += delta;
        if (_accum < IntervalSeconds) return;
        _accum = 0;

        var snapshot = Sts2Access.ReadSnapshot();
        snapshot.UploadsRuns = Config.UploadRuns;
        Latest = snapshot;
        LocalPlayer.SetCoop(snapshot.PlayerCount > 1);
        RewardContext.Character = snapshot.InRun ? snapshot.Character : null;
        var hpPct = snapshot.InRun && snapshot.MaxHp > 0
            ? snapshot.CurrentHp * 100.0 / snapshot.MaxHp
            : (double?)null;
        if (snapshot.Screen == "rest" && hpPct is { } now && RewardContext.HpPct is { } prev && now > prev)
            hpPct = prev;
        RewardContext.HpPct = hpPct;
        RewardContext.Screen = snapshot.Screen;
        Api.CodexScores.EnsureCharacter(snapshot.InRun ? snapshot.Character : null);
        Api.CodexScores.SetFilter(SpireCodexConfig.BracketKey);
        Api.Metrics.SetBracket(SpireCodexConfig.BracketKey);
        Replay.ReplayRecorder.NoteRun(snapshot);
        SnapshotWriter.Write(snapshot);
    }
}
