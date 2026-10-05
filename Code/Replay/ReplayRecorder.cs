using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpireCodex.Core;
using SpireCodex.Producer;

namespace SpireCodex.Replay;

public static class ReplayRecorder
{
    private static ReplayJournal? _journal;
    private static string? _seed;
    private static Snapshot? _lastInRun;
    private static List<ReplayLine>? _deckSnapshot;
    private static int _deckCount = -1;
    private static int _deckSig;
    private static volatile bool _deckDirty;
    private static List<DeckRemap.Entry>? _deckKeys;
    private static WeakReference<object>? _deckFirst;

    private static string? _shopSig;

    private static int _floor = -1;
    private static int _act = -1;

    public static void NoteFloor(int floor, int act)
    {
        if (floor >= 0) _floor = floor;
        if (act > 0) _act = act;
    }
    private static int _decisionId;
    private static readonly object Gate = new();

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpireCodex", "replays");

    public static bool Active => _journal != null;

    public static string? CurrentPath => _journal?.Path;

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            RecoverAbandoned();
            ReplayUploader.PruneOld();
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"replay: start failed: {e.Message}");
        }
    }

    public static void NoteRun(Snapshot snapshot)
    {
        try
        {
            if (!SpireCodexConfig.RecordReplays) { StopIfRunning("disabled"); return; }

            var seed = snapshot.InRun ? snapshot.Seed : null;
            if (seed == _seed)
            {
                if (snapshot.InRun)
                {
                    _lastInRun = snapshot;
                    RefreshDeckIfChanged(snapshot);
                    RecordShopIfChanged(snapshot);
                }
                return;
            }

            if (_journal != null)
            {
                var prior = _lastInRun;
                if (prior != null)
                    Finish(prior, ReplayHooks.PlayerDied ? "death"
                        : prior.IsGameOver ? "game_over" : "left_run");
                else StopIfRunning("run_changed");
            }

            _seed = seed;
            _lastInRun = null;
            _deckSnapshot = null;
            _deckCount = -1;
            _deckSig = 0;
            _deckDirty = false;
            _deckKeys = null;
            _deckFirst = null;
            if (string.IsNullOrEmpty(seed)) return;
            _lastInRun = snapshot;

            CardInstances.Reset();
            ReplayHooks.ResetRun();
            _floor = -1;
            _act = -1;
            Interlocked.Exchange(ref _decisionId, 0);
            _deckSnapshot = null;
            _deckCount = -1;
            _deckSig = 0;
            _deckDirty = false;

            var runState = Core.Sts2Access.LiveRunState;
            var runSeed = Reflect.GetString(Reflect.GetMember(runState, "Rng"), "StringSeed");
            var startTime = RunStartTime();
            if (runSeed == null || startTime == 0)
            {
                MainFile.Logger.Info(
                    $"replay: RUN IDENTITY INCOMPLETE (seed={runSeed ?? "?"}, start_time={startTime}); "
                    + "uploads will be rejected until this is fixed");
                runSeed ??= seed!;
                if (startTime == 0) startTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }

            var journal = ReplayJournal.Open(Dir, runSeed, startTime);
            if (journal == null) return;

            CardInstances.ResumeFrom(journal.LastCardId);
            Interlocked.Exchange(ref _decisionId, journal.LastDecisionId);
            CreatureSlots.ResumeFrom(journal.LastCreatureId);

            lock (Gate) _journal = journal;
            WriteHeader(snapshot, runSeed, startTime);

            var reloads = Reloads();
            AttemptId = reloads;
            NoteFloor(snapshot.TotalFloor, snapshot.Act);
            var resumedCombat = ReplayHooks.ResumeCombatIfInFight();
            if (journal.Resumed || reloads > 0)
                Line("resume")
                    ?.Set("reloads", reloads)
                    .Set("combat_id", resumedCombat)
                    .Set("wall_clock", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    .Set("run_time", snapshot.RunTime)
                    .Set("hp", snapshot.CurrentHp)
                    .Set("gold", snapshot.Gold)
                    .Set("deck_size", snapshot.DeckSize)
                    .Set("relics", HeldRelics())
                    .Set("potions", PotionBelt())
                    .Set("rng_state", RngState.Read())
                    .Emit();
            if (journal.Resumed) EmitDeckRemap(journal);
            RefreshDeckIfChanged(snapshot);
            MainFile.Logger.Info($"replay: recording {Path.GetFileName(journal.Path)}");
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"replay: NoteRun failed: {e.Message}");
        }
    }

    public static ReplayLine? Line(string kind)
    {
        if (_journal == null) return null;
        var line = new ReplayLine(kind);
        if (_floor >= 0) line.Set("floor", _floor).Set("act", _act > 0 ? _act : 1);
        else if (LiveStateProducer.Latest is { InRun: true } s)
            line.Set("floor", s.TotalFloor).Set("act", s.Act);
        return line;
    }

    public static void Emit(this ReplayLine? line)
    {
        if (line == null) return;
        _journal?.Write(line);
    }

    public static int NextDecisionId() => Interlocked.Increment(ref _decisionId);

    public static int AttemptId { get; private set; }

    public static string FloorKey => $"{(_act > 0 ? _act : 1)}.{(_floor >= 0 ? _floor : 0)}";

    private static void WriteHeader(Snapshot s, string seed, long startTime)
    {
        Line("header")
            ?.Set("replay_version", 7)
            .Set("run_schema_version", 9)
            .Set("seed", seed)
            .Set("build_id", Core.Sts2Version.Current.Split('+')[0])
            .Set("build_id_full", Core.Sts2Version.Current)
            .Set("mod_version", Api.ModVersion.Current)
            .Set("character", s.Character)
            .Set("ascension", s.Ascension)
            .Set("starting_max_hp", s.MaxHp > 0 ? s.MaxHp : (int?)null)
            .Set("game_mode", s.GameMode?.ToLowerInvariant())
            .Set("modifiers", s.Modifiers)
            .Set("mods", LoadedMods())
            .Set("harmony_owners", HarmonyOwners())
            .Set("full_console", FullConsole())
            .Set("start_time", startTime)
            .Set("platform_type", "steam")
            .Set("player_count", s.PlayerCount)
            .Set("reloads", Reloads())
            .Set("starting_deck", StartingDeck())
            .Set("starting_relics", s.Relics.Select(r => r.Id).ToList())
            .Set("relics", HeldRelics())
            .Set("potions", PotionBelt())
            .Set("rng_state", RngState.Read())
            .Emit();
    }

    private static List<ReplayLine>? LoadedMods()
    {
        try
        {
            var manager = Core.HookPatcher.FindType("MegaCrit.Sts2.Core.Modding.ModManager");
            var loaded = manager?.GetMethod("GetLoadedMods", System.Type.EmptyTypes)?.Invoke(null, null);
            if (loaded is not System.Collections.IEnumerable mods) return null;
            var rows = new List<ReplayLine>();
            foreach (var mod in mods)
            {
                var manifest = Reflect.GetMember(mod, "manifest");
                rows.Add(new ReplayLine("m")
                    .Set("id", Reflect.GetString(manifest, "id"))
                    .Set("version", Reflect.GetString(manifest, "version"))
                    .SetFlag("affects_gameplay", Reflect.GetBool(manifest, "affectsGameplay", true))
                    .Set("source", Reflect.GetMember(mod, "modSource")?.ToString()?.ToLowerInvariant()));
            }
            return rows;
        }
        catch { return null; }
    }

    private static List<string>? HarmonyOwners()
    {
        try
        {
            return HarmonyLib.Harmony.GetAllPatchedMethods()
                .SelectMany(m => HarmonyLib.Harmony.GetPatchInfo(m)?.Owners
                                 ?? (IEnumerable<string>)System.Array.Empty<string>())
                .Distinct()
                .OrderBy(o => o, System.StringComparer.Ordinal)
                .ToList();
        }
        catch { return null; }
    }

    private static bool? FullConsole()
    {
        try
        {
            var saves = Reflect.GetStatic(
                Core.HookPatcher.FindType("MegaCrit.Sts2.Core.Saves.SaveManager"), "Instance");
            return Reflect.GetMember(Reflect.GetMember(saves, "SettingsSave"), "FullConsole") as bool?;
        }
        catch { return null; }
    }

    private static int Reloads()
    {
        try
        {
            var state = Core.Sts2Access.LiveRunState;
            var mgr = Reflect.GetStatic(
                state?.GetType().Assembly.GetType("MegaCrit.Sts2.Core.Runs.RunManager"), "Instance");
            if (Reflect.GetMember(mgr, "NumReloads") is int pub) return pub;
            if (Reflect.GetMember(mgr, "_numReloads") is int priv) return priv;
        }
        catch { }
        return 0;
    }

    private static void RecordShopIfChanged(Snapshot s)
    {
        if (_journal == null) return;
        if (s.Shop is not { } shop)
        {
            _shopSig = null;
            return;
        }

        var sig = string.Join(",",
            shop.Cards.Select(c => $"c{c.Id}:{c.Cost}:{c.Stocked}:{c.OnSale}")
                .Concat(shop.Relics.Select(r => $"r{r.Id}:{r.Cost}:{r.Stocked}"))
                .Concat(shop.Potions.Select(p => $"p{p.Id}:{p.Cost}:{p.Stocked}")))
            + $"|{shop.Removal?.Cost}:{shop.Removal?.Stocked}";
        if (sig == _shopSig) return;
        _shopSig = sig;

        static List<ReplayLine> Items(List<ShopItemInfo> src) =>
            src.Select((it, i) => new ReplayLine("i")
                .Set("slot", i)
                .Set("id", it.Id)
                .Set("cost", it.Cost)
                .SetFlag("stocked", it.Stocked)
                .SetFlag("sale", it.OnSale)
                .Set("pool", it.Slot))
            .ToList();

        Line("shop")
            ?.Set("floor", s.TotalFloor)
            .Set("act", s.Act)
            .Set("gold", s.Gold)
            .Set("removal_cost", shop.Removal?.Cost)
            .Set("removal_stocked", shop.Removal?.Stocked)
            .Set("cards", Items(shop.Cards))
            .Set("relics", Items(shop.Relics))
            .Set("potions", Items(shop.Potions))
            .Emit();
    }

    private static long RunStartTime()
    {
        try
        {
            var state = Core.Sts2Access.LiveRunState;
            if (Reflect.GetMember(state, "StartTime") is long direct && direct > 0) return direct;
            var mgr = Reflect.GetStatic(
                state?.GetType().Assembly.GetType("MegaCrit.Sts2.Core.Runs.RunManager"), "Instance");
            if (Reflect.GetMember(mgr, "StartTime") is long pub && pub > 0) return pub;
            if (Reflect.GetMember(mgr, "_startTime") is long priv && priv > 0) return priv;
        }
        catch { }
        return 0;
    }

    private static void EmitDeckRemap(ReplayJournal journal)
    {
        try
        {
            var before = ReplayJournalScan.DeckEntries(journal.DeckLine);
            if (before.Count == 0) return;
            LiveDeck(out var after);
            if (after.Count == 0) return;

            var result = DeckRemap.Align(before, after);
            if (result.Pairs.Count == 0 && result.Ambiguous == 0) return;

            var rows = new List<ReplayLine>();
            foreach (var pair in result.Pairs)
                rows.Add(new ReplayLine("m").Set("from", pair.From).Set("to", pair.To));

            Line("remap")
                ?.Set("cards", rows.Count > 0 ? rows : null)
                .Set("ambiguous", result.Ambiguous > 0 ? result.Ambiguous : (int?)null)
                .SetFlag("exact", result.Exact)
                .Emit();
        }
        catch { }
    }

    private static void RefreshDeckIfChanged(Snapshot s)
    {
        if (_journal == null) return;
        if (DeckWasRenumbered())
        {
            RemapAfterInProcessReload(s);
            return;
        }
        var sig = DeckSignature(s);
        if (!_deckDirty && sig == _deckSig && s.Deck.Count == _deckCount) return;
        _deckDirty = false;
        _deckSig = sig;
        _deckCount = s.Deck.Count;
        _deckSnapshot = LiveDeck(out var keys);
        _deckKeys = keys;
        _deckFirst = NewDeckReference();
        Line("deck")?.Set("cards", _deckSnapshot).Emit();
    }

    private static bool DeckWasRenumbered()
    {
        try
        {
            if (_deckKeys == null || _deckKeys.Count == 0) return false;
            var live = FirstDeckCard();
            if (live == null) return false;
            if (_deckFirst != null && _deckFirst.TryGetTarget(out var prior))
                return !ReferenceEquals(prior, live);
            return true;
        }
        catch { }
        return false;
    }

    private static WeakReference<object>? NewDeckReference()
    {
        var first = FirstDeckCard();
        return first == null ? null : new WeakReference<object>(first);
    }

    private static object? FirstDeckCard()
    {
        var deck = Reflect.GetMember(Core.Sts2Access.LivePlayer, "Deck");
        if (Reflect.GetMember(deck, "Cards") is not System.Collections.IEnumerable cards)
            return null;
        foreach (var card in cards)
            if (card != null)
                return card;
        return null;
    }

    private static void RemapAfterInProcessReload(Snapshot s)
    {
        var before = _deckKeys;
        if (before == null || before.Count == 0) return;
        var rows = LiveDeck(out var after);
        if (after.Count == 0) return;

        var known = new HashSet<int>();
        foreach (var entry in before) known.Add(entry.C);
        foreach (var entry in after)
            if (known.Contains(entry.C))
            {
                _deckFirst = NewDeckReference();
                return;
            }

        var result = DeckRemap.Align(before, after);
        _deckSnapshot = rows;
        _deckKeys = after;
        _deckFirst = NewDeckReference();
        _deckDirty = false;
        _deckSig = DeckSignature(s);
        _deckCount = after.Count;

        if (result.Pairs.Count > 0 || result.Ambiguous > 0)
        {
            var pairs = new List<ReplayLine>();
            foreach (var pair in result.Pairs)
                pairs.Add(new ReplayLine("m").Set("from", pair.From).Set("to", pair.To));
            Line("remap")
                ?.Set("cards", pairs.Count > 0 ? pairs : null)
                .Set("ambiguous", result.Ambiguous > 0 ? result.Ambiguous : (int?)null)
                .SetFlag("exact", result.Exact)
                .SetFlag("in_process", true)
                .Set("rng_state", RngState.Read())
                .Emit();
        }
        Line("deck")?.Set("cards", rows).Emit();
    }

    public static void MarkDeckChanged() => _deckDirty = true;

    private static int DeckSignature(Snapshot s)
    {
        var sig = 17;
        foreach (var d in s.Deck)
        {
            sig = unchecked(sig * 31 + (d.Id?.GetHashCode() ?? 0));
            sig = unchecked(sig * 31 + (d.Upgraded ? 1 : 0));
            sig = unchecked(sig * 31 + (d.Enchantment?.GetHashCode() ?? 0));
        }
        return sig;
    }

    private static List<ReplayLine> StartingDeck() => LiveDeck();

    private static List<ReplayLine> LiveDeck() => LiveDeck(out _);

    private static List<ReplayLine> LiveDeck(out List<DeckRemap.Entry> entries)
    {
        var rows = new List<ReplayLine>();
        entries = new List<DeckRemap.Entry>();
        try
        {
            var deck = Reflect.GetMember(Core.Sts2Access.LivePlayer, "Deck");
            if (Reflect.GetMember(deck, "Cards") is not System.Collections.IEnumerable cards)
                return rows;
            foreach (var card in cards)
            {
                if (card == null) continue;
                var enchantment = Reflect.GetMember(card, "Enchantment");
                var instance = CardInstances.Of(card);
                var cardId = Core.Ids.Bare(Reflect.GetString(card, "Id"));
                var up = Reflect.GetInt(card, "CurrentUpgradeLevel", 0);
                var enchantId = enchantment == null
                    ? null : Core.Ids.Bare(Reflect.GetString(enchantment, "Id"));
                var amount = enchantment == null
                    ? 0 : Reflect.GetInt(enchantment, "Amount", 0);
                var addedFloor = Reflect.GetMember(card, "FloorAddedToDeck") is int floor
                    ? floor : (int?)null;
                rows.Add(new ReplayLine("c")
                    .Set("c", instance)
                    .Set("id", cardId)
                    .Set("up", up)
                    .Set("enchantment", enchantId)
                    .Set("amount", enchantment == null ? (int?)null : amount)
                    .Set("added_floor", addedFloor)
                    .Set("state", CardState(card)));
                entries.Add(new DeckRemap.Entry(
                    instance, ReplayJournalScan.Key(cardId, up, enchantId, amount),
                    ReplayJournalScan.Tag(addedFloor)));
            }
        }
        catch { }
        return rows;
    }

    private static List<ReplayLine>? HeldRelics()
    {
        try
        {
            if (Reflect.GetMember(Core.Sts2Access.LivePlayer, "Relics") is not System.Collections.IEnumerable relics)
                return null;
            var rows = new List<ReplayLine>();
            foreach (var relic in relics)
            {
                if (relic == null) continue;
                rows.Add(new ReplayLine("r")
                    .Set("id", Core.Ids.Bare(Reflect.GetString(relic, "Id")))
                    .Set("state", RelicState(relic)));
            }
            return rows;
        }
        catch { return null; }
    }

    private static ReplayLine? RelicState(object relic)
    {
        var props = SavedPropertiesOf(relic.GetType());
        if (props.Length == 0) return null;
        ReplayLine? row = null;
        foreach (var p in props)
        {
            object? value;
            try { value = StateValue(p.GetValue(relic)); }
            catch { continue; }
            if (value == null) continue;
            if (value is false && p.DeclaringType?.Name == "RelicModel") continue;
            (row ??= new ReplayLine("state", props.Length)).Set(Snake(p.Name), value);
        }
        return row;
    }

    private static object? StateValue(object? value) => value switch
    {
        null => null,
        string or bool or int or long or decimal or double or float => value,
        System.Collections.IEnumerable list => list.Cast<object?>()
            .Select(StateValue).OfType<string>().ToList(),
        _ when value.GetType().Name == "ModelId" => Core.Ids.Bare(value.ToString()),
        _ => Core.Ids.Bare(Reflect.GetString(value, "Id")) ?? value.ToString(),
    };

    private static List<ReplayLine>? PotionBelt()
    {
        try
        {
            if (Reflect.GetMember(Core.Sts2Access.LivePlayer, "PotionSlots") is not System.Collections.IEnumerable slots)
                return null;
            var rows = new List<ReplayLine>();
            var slot = 0;
            foreach (var potion in slots)
            {
                rows.Add(new ReplayLine("p")
                    .Set("slot", slot++)
                    .Set("id", potion == null ? null : Core.Ids.Bare(Reflect.GetString(potion, "Id"))));
            }
            return rows;
        }
        catch { return null; }
    }

    private static ReplayLine? CardState(object card)
    {
        var props = SavedPropertiesOf(card.GetType());
        if (props.Length == 0) return null;
        ReplayLine? row = null;
        foreach (var p in props)
        {
            object? value;
            try { value = p.GetValue(card); }
            catch { continue; }
            if (value == null) continue;
            (row ??= new ReplayLine("state", props.Length)).Set(Snake(p.Name), value);
        }
        return row;
    }

    private static readonly Dictionary<Type, System.Reflection.PropertyInfo[]> SavedProps = new();
    private static Type? _savedPropertyAttr;
    private static bool _savedPropertyAttrTried;

    private static System.Reflection.PropertyInfo[] SavedPropertiesOf(Type type)
    {
        lock (SavedProps)
        {
            if (SavedProps.TryGetValue(type, out var known)) return known;
            var found = Array.Empty<System.Reflection.PropertyInfo>();
            try
            {
                if (!_savedPropertyAttrTried)
                {
                    _savedPropertyAttrTried = true;
                    _savedPropertyAttr = HookPatcher.FindType(
                        "MegaCrit.Sts2.Core.Saves.Runs.SavedPropertyAttribute");
                    if (_savedPropertyAttr == null)
                        MainFile.Logger.Info(
                            "replay: SavedPropertyAttribute not found; card state omitted");
                }
                if (_savedPropertyAttr != null)
                {
                    var hits = new List<System.Reflection.PropertyInfo>();
                    foreach (var p in type.GetProperties(
                                 System.Reflection.BindingFlags.Public
                                 | System.Reflection.BindingFlags.NonPublic
                                 | System.Reflection.BindingFlags.Instance))
                    {
                        if (p.GetIndexParameters().Length > 0) continue;
                        if (Attribute.GetCustomAttribute(p, _savedPropertyAttr) == null) continue;
                        hits.Add(p);
                    }
                    hits.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                    found = hits.ToArray();
                }
            }
            catch { }
            SavedProps[type] = found;
            return found;
        }
    }

    private static string Snake(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (char.IsUpper(ch))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(ch));
            }
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private static void StopIfRunning(string reason)
    {
        ReplayJournal? journal;
        lock (Gate)
        {
            journal = _journal;
            _journal = null;
        }
        _seed = null;
        if (journal == null) return;

        _journal = journal;
        ReplayHooks.CloseOpenDecision();
        _journal = null;

        var terminal = new ReplayLine("end").Set("terminal_reason", reason);
        _ = Task.Run(async () =>
        {
            await journal.CloseAsync(terminal).ConfigureAwait(false);
            journal.Dispose();
        });
    }

    public static void Finish(Snapshot s, string reason)
    {
        ReplayJournal? journal;
        lock (Gate)
        {
            journal = _journal;
            _journal = null;
        }
        _seed = null;
        if (journal == null) return;

        var terminal = new ReplayLine("end")
            .Set("terminal_reason", reason)
            .Set("run_time", s.RunTime)
            .Set("floors", s.TotalFloor)
            .SetFlag("is_game_over", s.IsGameOver || ReplayHooks.PlayerDied)
            .Set("hp", ReplayHooks.PlayerDied ? 0 : s.CurrentHp)
            .Set("max_hp", s.MaxHp)
            .Set("final_deck", (object?)_deckSnapshot ?? s.Deck.Select(d => d.Id).ToList())
            .Set("final_relics", s.Relics.Select(r => r.Id).ToList());

        _ = Task.Run(async () =>
        {
            await journal.CloseAsync(terminal).ConfigureAwait(false);
            journal.Dispose();
        });
    }

    private static void RecoverAbandoned()
    {
        string[] files;
        try { files = Directory.GetFiles(Dir, "*.jsonl"); }
        catch { return; }

        foreach (var file in files)
        {
            try
            {
                if (HasTerminal(file)) continue;
                if (!EndsWithNewline(file)) File.AppendAllText(file, "\n");
                var last = ReplayJournal.LastSequence(file);
                var open = ReplayJournalScan.OpenCombat(file);
                File.AppendAllText(file,
                    "{\"t\":\"end\",\"s\":" + (last + 1) + ",\"terminal_reason\":\"interrupted\"," +
                    "\"capture_status\":\"truncated\"" +
                    (last >= 0 ? ",\"last_s\":" + last : "") +
                    (open != null ? ",\"open_combat\":" + JsonSerializer.Serialize(open) : "") + "}\n");
                MainFile.Logger.Info($"replay: recovered {Path.GetFileName(file)} (interrupted)");
            }
            catch {  }
        }
    }

    private static bool EndsWithNewline(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0) return true;
            fs.Seek(-1, SeekOrigin.End);
            return fs.ReadByte() == '\n';
        }
        catch { return true; }
    }

    private static bool HasTerminal(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var take = (int)Math.Min(fs.Length, 4096);
            if (take == 0) return false;
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            var read = fs.Read(buf, 0, take);
            var tail = System.Text.Encoding.UTF8.GetString(buf, 0, read);
            var at = tail.LastIndexOf("\"t\":\"end\"", StringComparison.Ordinal);
            return at >= 0 && tail.IndexOf('\n', at) >= 0;
        }
        catch { return true; }
    }
}
