using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.AutoSlay.Handlers;
using MegaCrit.Sts2.Core.AutoSlay.Handlers.Rooms;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace SpireCodex.Autopilot;

// Plays the current run with the engine behind `sim-cli serve`. Map moves follow the engine's
// ranked paths (`advise`), fights follow the information-set searcher (`run_search`) on a run
// the engine loads from the live save at fight start and mirrors action by action, and every
// other room and screen goes through the game's own AutoSlay handlers. Toggle with the
// Autopilot hotkey while a run is in progress.
public partial class Autopilot : Node
{
    public static bool Enabled { get; private set; }

    private static Autopilot? instance;
    private EngineBridge? _engine;
    private CancellationTokenSource? _cts;
    private AutoSlayer? _slayer;
    private IDisposable? _selectorScope;

    public static void Start()
    {
        if (instance is not null || Engine.GetMainLoop() is not SceneTree tree) return;
        instance = new Autopilot { Name = "SpireCodexAutopilot" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, instance);
        AdviceHud.Start();
    }

    public override void _UnhandledInput(InputEvent input)
    {
        if (input is not InputEventKey { Pressed: true, Echo: false } key) return;
        var configured = SpireCodexConfig.AutopilotKeycode;
        if (configured == Key.None || (key.Keycode != configured && key.PhysicalKeycode != configured)) return;
        if (Enabled) Stop("stopped by hotkey");
        else Begin();
        GetViewport().SetInputAsHandled();
    }

    private void Begin()
    {
        if (RunManager.Instance?.DebugOnlyGetState() is null)
        {
            Advice.Set(ref Advice.Status, "no run in progress");
            MainFile.Logger.Info("autopilot: no run in progress");
            return;
        }
        _engine = EngineBridge.Open();
        if (_engine is null)
        {
            Advice.Set(ref Advice.Status, $"engine missing, see {EngineBridge.SettingsPath}");
            return;
        }
        Enabled = true;
        Advice.Set(ref Advice.Status, "on");
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            try { await PlayAsync(token); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                MainFile.Logger.Warn($"autopilot stopped: {error.Message}");
                Advice.Set(ref Advice.Status, $"stopped: {error.Message}");
            }
            finally { Enabled = false; }
        });
    }

    private void Stop(string reason)
    {
        Enabled = false;
        _cts?.Cancel();
        _cts = null;
        _selectorScope?.Dispose();
        _selectorScope = null;
        _engine?.Dispose();
        _engine = null;
        Advice.Set(ref Advice.Status, reason);
        MainFile.Logger.Info($"autopilot: {reason}");
    }

    private static string SaveText() => JsonSerializationUtility.ToJson(RunManager.Instance.ToSave(null));

    private static Player LocalPlayerOf(RunState state) => LocalContext.GetMe(state) ?? throw new InvalidOperationException("no local player");

    private async Task PlayAsync(CancellationToken ct)
    {
        var state = RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no run in progress");
        _slayer = new AutoSlayer();
        var random = new Rng((uint)System.Environment.TickCount);
        AccessTools.Field(typeof(AutoSlayer), "_random").SetValue(_slayer, random);
        AccessTools.Field(typeof(AutoSlayer), "_watchdog").SetValue(_slayer, new Watchdog());
        _selectorScope = CardSelectCmd.UseSelector(new AutoSlayCardSelector(random));
        var rooms = new Dictionary<RoomType, IRoomHandler>
        {
            [RoomType.Event] = new EventRoomHandler(),
            [RoomType.Shop] = new ShopRoomHandler(),
            [RoomType.Treasure] = new TreasureRoomHandler(),
            [RoomType.RestSite] = new RestSiteRoomHandler(),
        };
        await WaitHelper.Until(() => state.CurrentRoom != null && state.CurrentRoom.RoomType != RoomType.Unassigned, ct, TimeSpan.FromSeconds(30), "Room type not assigned");
        while (Enabled && !ct.IsCancellationRequested)
        {
            var roomType = state.CurrentRoom?.RoomType ?? RoomType.Unassigned;
            if (roomType is RoomType.Monster or RoomType.Elite or RoomType.Boss)
            {
                await PlayFightAsync(state, ct);
                await Private("WaitForRewardsScreenAsync", ct);
            }
            else if (rooms.TryGetValue(roomType, out var handler))
            {
                await WaitHelper.WithTimeout(token => handler.HandleAsync(random, token), handler.Timeout, ct);
                await Task.Delay(500, ct);
            }
            await Private("DrainOverlayScreensAsync", ct);
            if (roomType == RoomType.RestSite) await Private("ClickRestSiteProceedIfNeeded", ct);
            if (roomType == RoomType.Event) await Private("ClickEventProceedIfNeeded", ct);
            if (roomType == RoomType.Boss)
            {
                var finalBoss = state.Map.SecondBossMapPoint == null || state.CurrentMapCoord != state.Map.BossMapPoint.coord;
                if (finalBoss && state.CurrentActIndex >= 2)
                {
                    Advice.Set(ref Advice.Status, "run over");
                    return;
                }
                await WaitHelper.Until(() => state.CurrentRoom != null && state.CurrentRoom.RoomType != RoomType.Boss, ct, TimeSpan.FromSeconds(60), "Act transition did not complete");
                await Private("DrainOverlayScreensAsync", ct);
            }
            await MoveAsync(state, ct);
        }
    }

    private Task Private(string name, CancellationToken ct)
    {
        var method = AccessTools.Method(typeof(AutoSlayer), name) ?? throw new InvalidOperationException($"AutoSlayer.{name} is missing");
        return (Task)method.Invoke(_slayer, [ct])!;
    }

    private async Task MoveAsync(RunState state, CancellationToken ct)
    {
        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        var runNode = root.GetNode<NRun>("/root/Game/RootSceneContainer/Run");
        await WaitHelper.Until(() => runNode.GlobalUi.MapScreen.IsVisibleInTree(), ct, TimeSpan.FromSeconds(30), "Map screen not visible");
        var points = UiHelper.FindAll<NMapPoint>(runNode.GlobalUi.MapScreen);
        MapCoord? chosen = null;
        try
        {
            var advice = _engine!.Advise(SaveText());
            if (EngineBridge.Error(advice) is { } error)
            {
                Advice.Set(ref Advice.Map, $"engine refused: {error}");
            }
            else if (advice["advice"]?["paths"] is JsonArray { Count: > 0 } paths)
            {
                var best = (JsonObject)paths[0]!;
                var route = (JsonArray)best["route"]!;
                var first = (JsonArray)route[0]!;
                chosen = new MapCoord(first[0]!.GetValue<int>(), first[1]!.GetValue<int>());
                Advice.Set(ref Advice.Map, $"route {string.Join(" > ", route.Select(step => $"{step![0]},{step[1]}"))} fights {best["fights_won"]}-{best["fights_lost"]} hp {best["hp_end"]}");
                if (advice["advice"]?["picks"]?["picks"] is JsonArray { Count: > 0 } picks)
                    Advice.Set(ref Advice.Picks, picks[0]!["label"]?.ToString() ?? "");
            }
        }
        catch (Exception error)
        {
            Advice.Set(ref Advice.Map, $"advice failed: {error.Message}");
        }
        NMapPoint? next = null;
        if (chosen is { } coord) next = points.FirstOrDefault(point => point.Point.coord.Equals(coord));
        if (next is null)
        {
            var visited = state.VisitedMapCoords;
            if (visited.Count == 0)
                next = points.First(point => point.Point.coord.row == 0);
            else
            {
                var last = visited[visited.Count - 1];
                var current = points.First(point => point.Point.coord.Equals(last));
                var child = current.Point.Children.First();
                next = points.First(point => point.Point.coord.Equals(child.coord));
            }
        }
        await WaitHelper.Until(() => next.IsEnabled, ct, TimeSpan.FromSeconds(10), "Map point not enabled");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRoomEntered() => entered.TrySetResult();
        RunManager.Instance.RoomEntered += OnRoomEntered;
        try
        {
            await UiHelper.Click(next);
            await WaitHelper.ForTask(entered.Task, ct, TimeSpan.FromSeconds(30), "Room not entered after map click");
        }
        finally
        {
            RunManager.Instance.RoomEntered -= OnRoomEntered;
        }
    }

    private async Task PlayFightAsync(RunState state, CancellationToken ct)
    {
        await WaitHelper.Until(() => CombatManager.Instance.IsInProgress, ct, TimeSpan.FromSeconds(30), "Combat not started");
        var player = LocalPlayerOf(state);
        long? run = null;
        try
        {
            run = _engine!.RunLoad(SaveText());
        }
        catch (Exception error)
        {
            Advice.Set(ref Advice.Combat, $"engine could not load the fight: {error.Message}");
        }
        var turns = 0;
        while (CombatManager.Instance.IsInProgress && turns < 100 && Enabled)
        {
            ct.ThrowIfCancellationRequested();
            turns++;
            await WaitHelper.Until(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play || !CombatManager.Instance.IsInProgress, ct, TimeSpan.FromSeconds(30), "Play phase not started");
            if (!CombatManager.Instance.IsInProgress) break;
            var played = 0;
            while (played < 50 && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play && CombatManager.Instance.IsInProgress)
            {
                ct.ThrowIfCancellationRequested();
                var action = run is { } handle ? NextAction(handle) : null;
                if (action is null || action["end_turn"] is not null)
                {
                    if (action is not null && run is { } ending) Mirror(ending, action, ref run);
                    break;
                }
                var hand = PileType.Hand.GetPile(player).Cards;
                var index = action["play"]!.GetValue<int>();
                if (index >= hand.Count)
                {
                    Advice.Set(ref Advice.Combat, $"engine named hand card {index} of {hand.Count}, ending turn");
                    break;
                }
                var card = hand[index];
                Creature? target = null;
                if (action["target"] is JsonValue targetValue && targetValue.TryGetValue<int>(out var slot))
                {
                    var enemies = card.CombatState?.Enemies ?? Array.Empty<Creature>();
                    target = slot < enemies.Count ? enemies[slot] : card.CombatState?.HittableEnemies.FirstOrDefault();
                }
                Advice.Set(ref Advice.Combat, $"play {card.Id.Entry}{(target is null ? "" : " on " + target.Name)}");
                await CardCmd.AutoPlay(new BlockingPlayerChoiceContext(), card, target);
                played++;
                if (run is { } current) Mirror(current, action, ref run);
                await Task.Delay(100, ct);
            }
            if (player.PlayerCombatState?.Phase == PlayerTurnPhase.Play && CombatManager.Instance.IsInProgress)
                PlayerCmd.EndTurn(player, canBackOut: false);
        }
        await WaitHelper.Until(() => !CombatManager.Instance.IsInProgress, ct, TimeSpan.FromSeconds(30), "Combat did not end");
        if (run is { } done)
        {
            try { _engine!.RunRelease(done); } catch (Exception) { }
        }
    }

    // Asks the searcher for the next fight action on the engine's copy of the fight.
    private JsonObject? NextAction(long run)
    {
        try
        {
            var legal = _engine!.RunLegal(run);
            if (legal.Count == 0) return null;
            if (legal.Count == 1) return (JsonObject)legal[0]!["action"]!;
            var report = _engine.RunSearch(run);
            if (EngineBridge.Error(report) is { } error)
            {
                Advice.Set(ref Advice.Combat, $"search refused: {error}");
                return null;
            }
            var actions = report["best"]?["actions"] as JsonArray ?? report["fair"]?["actions"] as JsonArray;
            if (actions is null || actions.Count == 0) return null;
            var chosen = (JsonObject)actions[0]!;
            foreach (var candidate in legal)
                if (JsonNode.DeepEquals(candidate!["action"], chosen)) return chosen;
            return (JsonObject)legal[0]!["action"]!;
        }
        catch (Exception error)
        {
            Advice.Set(ref Advice.Combat, $"engine lost the fight: {error.Message}");
            return null;
        }
    }

    // Applies the action on the engine's copy so the next search starts from the same state the
    // game is in; card choices the game made at random are filled with the first option.
    private void Mirror(long run, JsonObject action, ref long? handle)
    {
        try
        {
            var wrapped = new JsonObject { ["kind"] = "combat", ["action"] = action.DeepClone() };
            var choices = new JsonArray();
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var response = _engine!.RunApply(run, wrapped);
                if (response["error"]?.ToString() == "required_choices")
                {
                    var options = (JsonArray)response["options"]!;
                    choices.Add(options[0]!.DeepClone());
                    ((JsonObject)wrapped["action"]!)["choices"] = choices.DeepClone();
                    continue;
                }
                if (EngineBridge.Error(response) is { } error)
                {
                    Advice.Set(ref Advice.Combat, $"engine refused the mirror: {error}");
                    handle = null;
                }
                return;
            }
            handle = null;
        }
        catch (Exception error)
        {
            Advice.Set(ref Advice.Combat, $"engine lost the fight: {error.Message}");
            handle = null;
        }
    }
}
