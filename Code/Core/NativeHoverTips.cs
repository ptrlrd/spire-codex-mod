using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using SpireCodex.Api;

namespace SpireCodex.Core;

internal static class NativeHoverTips
{
    private static Type? _hoverTipType;
    private static Type? _iHoverTipType;
    private static Type? _cardHolderType;
    private static Type? _relicType;
    private static Type? _hoverTipSetType;
    private static Type? _portraitTipType;
    private static Type? _restButtonType;
    private static Type? _skipButtonType;
    private static MethodBase? _createAndShowSingle;
    private static MethodBase? _hoverTipRemove;
    private static bool _resolved;

    public static void Apply(Harmony harmony)
    {
        try
        {
            Resolve();
            var target = FindCreateAndShow();
            if (target == null) { Diag("CreateAndShow(IEnumerable<IHoverTip>) not found; native tips disabled"); return; }
            var prefix = typeof(NativeHoverTips).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Diag("native hover-tip patch applied");

            _createAndShowSingle = _hoverTipSetType!.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "CreateAndShow"
                    && m.GetParameters().Length == 3
                    && m.GetParameters()[1].ParameterType == _iHoverTipType);
            _hoverTipRemove = _hoverTipSetType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Remove" && m.GetParameters().Length == 1);

            ApplyPortraitPatch(harmony);
            ApplyRestSitePatch(harmony);
        }
        catch (Exception e) { Diag($"apply failed: {e.GetType().Name}: {e.Message}"); }
    }

    private static void ApplyPortraitPatch(Harmony harmony)
    {
        try
        {
            if (_portraitTipType == null) { Diag("portrait tip type not found; portrait stats disabled"); return; }
            var init = _portraitTipType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Instance);
            var focus = _portraitTipType.GetMethod("OnFocus", BindingFlags.NonPublic | BindingFlags.Instance);
            if (init == null || focus == null) { Diag("portrait Initialize/OnFocus not found; portrait stats disabled"); return; }
            harmony.Patch(init, postfix: new HarmonyMethod(
                typeof(NativeHoverTips).GetMethod(nameof(PortraitInitPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(focus, postfix: new HarmonyMethod(
                typeof(NativeHoverTips).GetMethod(nameof(PortraitFocusPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            Diag("portrait patch applied");
        }
        catch (Exception e) { Diag($"portrait patch failed: {e.GetType().Name}: {e.Message}"); }
    }

    private static void ApplyRestSitePatch(Harmony harmony)
    {
        try
        {
            if (_restButtonType == null || _createAndShowSingle == null || _hoverTipRemove == null)
            {
                Diag("rest-site types not found; campfire stats disabled");
                return;
            }
            var focus = FindDeclaredMethod(_restButtonType, "OnFocus");
            var unfocus = FindDeclaredMethod(_restButtonType, "OnUnfocus");
            if (focus == null || unfocus == null)
            {
                Diag("rest-site focus hooks not found; campfire stats disabled");
                return;
            }
            harmony.Patch(focus, postfix: new HarmonyMethod(
                typeof(NativeHoverTips).GetMethod(nameof(RestFocusPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(unfocus, postfix: new HarmonyMethod(
                typeof(NativeHoverTips).GetMethod(nameof(RestUnfocusPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            Diag("rest-site patch applied");
        }
        catch (Exception e) { Diag($"rest-site patch failed: {e.GetType().Name}: {e.Message}"); }
    }

    private static MethodInfo? FindDeclaredMethod(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var m = t.GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (m != null) return m;
        }
        return null;
    }

    private static void RestFocusPostfix(object __instance)
    {
        try
        {
            if (!SpireCodexConfig.ShowHoverTips) return;
            if (__instance is not Godot.Control owner) return;
            _hoverTipRemove?.Invoke(null, new object[] { owner });
            var text = BuildRestText(__instance);
            if (text == null) return;
            var tip = BuildTip(null, text);
            if (tip == null) return;
            var alignType = _createAndShowSingle!.GetParameters()[2].ParameterType;
            var set = _createAndShowSingle.Invoke(null, new[] { owner, tip, Enum.ToObject(alignType, 0) });
            if (set is Godot.Control tipSet)
                tipSet.GlobalPosition = owner.GlobalPosition + new Godot.Vector2(0f, owner.Size.Y + 16f);
        }
        catch (Exception e) { Diag($"rest focus postfix error: {e.GetType().Name}: {e.Message}"); }
    }

    private static void RestUnfocusPostfix(object __instance)
    {
        try
        {
            if (_restButtonType?.IsInstanceOfType(__instance) != true) return;
            _hoverTipRemove?.Invoke(null, new[] { __instance });
        }
        catch {  }
    }

    private static string? BuildRestText(object owner)
    {
        CommunityStats.EnsureLoaded();
        var option = Reflect.GetMember(owner, "Option");
        var typeName = option?.GetType().Name;
        const string suffix = "RestSiteOption";
        if (typeName == null || !typeName.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var key = typeName.Substring(0, typeName.Length - suffix.Length).ToUpperInvariant();
        var rc = CommunityStats.Rest(key);
        if (rc == null) return null;

        var sb = new StringBuilder();
        sb.Append(Logo).Append('\n');
        sb.Append(Loc.F("hover_rest_picked", rc.Label, rc.Pct));
        var hp = RewardContext.HpPct;
        if (hp is { } h && rc.PctLowHp is { } lo && rc.PctHighHp is { } hi)
        {
            var band = h < 50 ? lo : hi;
            sb.Append(Loc.F("hover_rest_at_hp", h, band));
        }
        if (rc.WinRate is { } wr)
            sb.Append(Loc.F("hover_rest_win_rate", wr));
        return sb.ToString().TrimEnd();
    }

    private static void PortraitInitPostfix(object __instance)
    {
        try
        {
            if (!SpireCodexConfig.ShowHoverTips) return;
            if (!Reflect.GetBool(__instance, "ShowTip") && __instance is Godot.Control c)
                c.FocusMode = Godot.Control.FocusModeEnum.All;
        }
        catch (Exception e) { Diag($"portrait init postfix error: {e.Message}"); }
    }

    private static void PortraitFocusPostfix(object __instance)
    {
        try
        {
            if (!SpireCodexConfig.ShowHoverTips) return;
            if (Reflect.GetBool(__instance, "ShowTip")) return;
            if (_createAndShowSingle == null || __instance is not Godot.Control owner) return;
            var text = BuildCharacterText();
            if (text == null) return;
            var tip = BuildTip(null, text);
            if (tip == null) return;

            var alignType = _createAndShowSingle.GetParameters()[2].ParameterType;
            var set = _createAndShowSingle.Invoke(null, new[] { owner, tip, Enum.ToObject(alignType, 0) });
            if (set is Godot.Control tipSet)
                tipSet.GlobalPosition = owner.GlobalPosition + new Godot.Vector2(0f, owner.Size.Y + 20f);
        }
        catch (Exception e) { Diag($"portrait focus postfix error: {e.GetType().Name}: {e.Message}"); }
    }

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        var sts2 = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetType("MegaCrit.Sts2.Core.HoverTips.HoverTip") != null);
        _hoverTipType = sts2?.GetType("MegaCrit.Sts2.Core.HoverTips.HoverTip");
        _iHoverTipType = sts2?.GetType("MegaCrit.Sts2.Core.HoverTips.IHoverTip");
        _cardHolderType = sts2?.GetType("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder");
        _relicType = sts2?.GetType("MegaCrit.Sts2.Core.Nodes.Relics.NRelic");
        _hoverTipSetType = sts2?.GetType("MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet");
        _portraitTipType = sts2?.GetType("MegaCrit.sts2.Core.Nodes.TopBar.NTopBarPortraitTip")
            ?? sts2?.GetType("MegaCrit.Sts2.Core.Nodes.TopBar.NTopBarPortraitTip");
        _restButtonType = sts2?.GetType("MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton");
        _skipButtonType = sts2?.GetType(
            "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChoiceSelectionSkipButton");
    }

    private static MethodBase? FindCreateAndShow()
    {
        if (_hoverTipSetType == null || _iHoverTipType == null) return null;
        var enumerableOfTip = typeof(IEnumerable<>).MakeGenericType(_iHoverTipType);
        return _hoverTipSetType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "CreateAndShow"
                && m.GetParameters().Length == 3
                && m.GetParameters()[1].ParameterType == enumerableOfTip);
    }

    private const string Logo = "[color=#ffd34d][b]Spire[/b][/color] [color=#ffffff][b]Codex[/b][/color]";

    private static void Prefix(object[] __args)
    {
        try
        {
            if (_iHoverTipType == null || __args.Length < 2) return;
            if (!SpireCodexConfig.ShowHoverTips) return;
            var owner = __args[0];
            if (owner == null) return;
            if (ContainsOurTip(__args[1])) return;

            var text = BuildTipText(owner);
            if (text == null) return;
            var tip = BuildTip(null, text);
            if (tip == null) return;

            __args[1] = Append(__args[1], tip);
        }
        catch (Exception e) { Diag($"prefix error: {e.GetType().Name}: {e.Message}"); }
    }

    private static string? BuildTipText(object owner)
    {
        PersonalStats.EnsureLoaded();

        if (_skipButtonType?.IsInstanceOfType(owner) == true && CodexScores.Skip is { } sk)
            return BuildSkipTip(sk);

        var model = ResolveCardModel(owner);
        if (model != null)
        {
            var id = Bare(Reflect.GetString(model, "Id"));
            if (id == null || CodexScores.Card(id) is not { Picks: > 0 } sc) return null;
            var text = BuildStats(sc, CardStatsCache.Get(id), id == RewardContext.BestCardId, showElo: true);
            if (PersonalStats.Card(id) is { Offered: > 0 } you)
                text += "\n" + Loc.F("hover_you_kept", Pct(you), you.Picked, you.Offered);
            return text;
        }

        model = ResolveRelicModel(owner);
        if (model != null)
        {
            var id = Bare(Reflect.GetString(model, "Id"));
            if (id == null || CodexScores.Relic(id) is not { Picks: > 0 } sc) return null;
            var text = BuildStats(sc, RelicStatsCache.Get(id), id == RewardContext.BestRelicId, showElo: false);
            if (CommunityStats.Ancient(id) is { } anc)
                text += "\n" + Loc.F("hover_ancient_taken", anc.TakeRate, anc.Offered);
            if (PersonalStats.Ancient(id) is { Offered: > 0 } youAnc)
                text += "\n" + Loc.F("hover_you_took", Pct(youAnc), youAnc.Picked, youAnc.Offered);
            return text;
        }

        model = ResolvePotionModel(owner);
        if (model != null)
        {
            var id = Bare(Reflect.GetString(model, "Id"));
            if (id == null || CodexScores.Potion(id) is not { Picks: > 0 } sc) return null;
            return BuildStats(sc, PotionStatsCache.Get(id), isBest: false, showElo: false);
        }

        var typeName = owner.GetType().Name;
        if (typeName == "NMerchantCardRemoval") return BuildRemovalText();
        if (typeName == "NEventOptionButton") return BuildEventOptionText(owner);
        if (typeName.Contains("Portrait")) return BuildCharacterText();

        DiagOwnerOnce(owner);
        return null;
    }

    private static string BuildSkipTip(SkipScore sk)
    {
        var act = Producer.LiveStateProducer.Latest?.Act ?? 0;
        var rate = act > 0 ? sk.RateForAct(act) : sk.SkipRate;
        var sb = new StringBuilder();
        sb.Append($"{Logo}\n");
        sb.Append(RewardContext.SkipWins
            ? $"[color=#86e08a][b]{Loc.T("hover_skip_yes")}[/b][/color]\n"
            : $"[color=#e08a86][b]{Loc.T("hover_skip_no")}[/b][/color]\n");
        sb.Append(Loc.F("hover_codex_elo", sk.Elo));
        sb.Append(act > 0
            ? Loc.F("hover_skip_rate_act", rate, act)
            : Loc.F("hover_skip_rate", rate));
        sb.Append(Loc.F("hover_skip_sample", sk.Skipped, sk.Screens));
        return sb.ToString();
    }

    private static string BuildStats(EntityScore sc, CardStats? full, bool isBest, bool showElo)
    {
        var tier = Ranks.Tier(sc.Score);
        var character = RewardContext.Character;
        var sb = new StringBuilder();
        sb.Append(Logo).Append('\n');
        if (isBest) sb.Append($"[color=#ffd34d]{Loc.T("hover_best_pick")}[/color]\n");
        sb.Append($"[color={TierHex(tier)}]{Loc.F("hover_tier", tier)}[/color]\n");
        if (showElo && sc.Elo is { } elo) sb.Append(Loc.F("hover_codex_elo", elo));
        sb.Append(Loc.F("hover_codex_score", sc.Score));

        double wr;
        double? delta = null;
        CharStat? mine = null;
        if (full != null && !string.IsNullOrEmpty(character))
            foreach (var c in full.ByCharacter)
                if (c.Character == character) { mine = c; break; }
        if (mine != null) { wr = mine.WinRate; delta = wr - full!.BaselineWinRate; }
        else if (sc.Scope == "character") { wr = sc.WinRate; if (full != null) delta = wr - full.BaselineWinRate; }
        else if (full != null) { wr = full.WinRate; delta = wr - full.BaselineWinRate; }
        else wr = sc.WinRate;

        sb.Append(Loc.F("hover_win_rate", wr));
        if (delta is { } d)
        {
            var dc = d >= 0 ? "#86e08a" : "#e08a86";
            sb.Append($"  [color={dc}]{Loc.F("hover_vs_base", d >= 0 ? "+" : "", d)}[/color]");
        }
        sb.Append('\n');

        if (full is { PickRate: > 0 }) sb.Append(Loc.F("hover_pick_rate", full.PickRate));
        return sb.ToString().TrimEnd();
    }

    private static readonly HashSet<string> EventDiagSeen = new();

    private static string? BuildEventOptionText(object owner)
    {
        CommunityStats.EnsureLoaded();
        if (CommunityStats.Data == null) { EventDiag("(any)", "community stats not loaded yet"); return null; }
        var evId = Bare(Reflect.GetString(Reflect.GetMember(owner, "Event"), "Id"));
        var rawKey = Reflect.GetString(Reflect.GetMember(owner, "Option"), "TextKey");
        var key = rawKey?.Substring(rawKey.LastIndexOf('.') + 1).ToUpperInvariant();
        if (evId == null || string.IsNullOrEmpty(key))
        {
            EventDiag(evId ?? "(no-event-id)", $"missing identifiers (key={key ?? "null"})");
            return null;
        }

        if (BuildAncientOptionText(key) is { } ancientTip) return ancientTip;

        var ev = CommunityStats.Event(evId);
        if (ev == null || ev.Total <= 0)
        {
            EventDiag(evId, $"event not in community data (key={key})");
            return null;
        }

        var count = -1;
        foreach (var o in ev.Options)
            if (o.Id == key) { count = o.Count; break; }
        if (count < 0)
        {
            count = 0;
            foreach (var o in ev.Options)
                if (o.Id.StartsWith(key + "_", StringComparison.Ordinal)) count += o.Count;
            if (count == 0)
            {
                EventDiag(evId, $"option key {key} not in [{string.Join(",", System.Linq.Enumerable.Select(ev.Options, o => o.Id))}]");
                return null;
            }
        }
        var pct = count * 100.0 / ev.Total;

        var sb = new StringBuilder();
        sb.Append(Logo).Append('\n');
        sb.Append(Loc.F("hover_players_pick", pct, count, ev.Total));
        return sb.ToString();
    }

    private static string? BuildAncientOptionText(string relicId)
    {
        var anc = CommunityStats.Ancient(relicId);
        if (anc == null) return null;

        var text = $"{Logo}\n" + Loc.F("hover_ancient_taken", anc.TakeRate, anc.Offered);
        if (PersonalStats.Ancient(relicId) is { Offered: > 0 } you)
            text += "\n" + Loc.F("hover_you_took", Pct(you), you.Picked, you.Offered);
        return text;
    }

    private static int Pct(UserPick u) => (int)System.Math.Round(u.Picked * 100.0 / u.Offered);

    private static void EventDiag(string evId, string why)
    {
        if (EventDiagSeen.Add($"{evId}|{why}")) Diag($"event tip miss: {evId}: {why}");
    }

    private static string? BuildRemovalText()
    {
        CommunityStats.EnsureLoaded();
        var data = CommunityStats.Data;
        if (data == null || data.MostRemoved.Count == 0) return null;
        var sb = new StringBuilder();
        sb.Append(Logo).Append('\n');
        sb.Append(Loc.T("hover_most_removed"));
        var n = Math.Min(3, data.MostRemoved.Count);
        for (var i = 0; i < n; i++)
        {
            var r = data.MostRemoved[i];
            sb.Append(Loc.F("hover_removal_row", i + 1, r.Name, r.Pct));
        }
        return sb.ToString().TrimEnd();
    }

    private static bool _portraitDiagged;

    private static string? BuildCharacterText()
    {
        var character = RewardContext.Character;
        if (string.IsNullOrEmpty(character)) return null;
        CommunityStats.EnsureLoaded();
        LocalStats.EnsureLoaded();
        var community = CommunityStats.Character(character);
        var mine = LocalStats.For(character);
        if (community == null && mine == null)
        {
            if (!_portraitDiagged)
            {
                _portraitDiagged = true;
                Diag($"portrait tip: no data yet (char={character}, " +
                     $"community={(CommunityStats.Data == null ? "unloaded" : "loaded-no-match")}, " +
                     $"local={(LocalStats.For(character) == null ? "none" : "ok")})");
            }
            return null;
        }
        _portraitDiagged = false;

        var sb = new StringBuilder();
        sb.Append(Logo).Append('\n');
        sb.Append($"[b]{Loc.CharacterName(character) ?? Pretty(character)}[/b]\n");
        if (mine != null)
            sb.Append(Loc.F("hover_your_win_rate", mine.WinRate, mine.Runs));
        if (community != null)
            sb.Append(Loc.F("hover_community_win_rate", community.WinRate, community.Runs, community.Share));
        return sb.ToString().TrimEnd();
    }

    private static object? BuildTip(string? title, string description)
    {
        if (_hoverTipType == null) return null;
        var box = Activator.CreateInstance(_hoverTipType);
        if (box == null) return null;
        foreach (var f in _hoverTipType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            switch (Strip(f.Name))
            {
                case "title": if (title != null && f.FieldType == typeof(string)) f.SetValue(box, title); break;
                case "description": if (f.FieldType == typeof(string)) f.SetValue(box, description); break;
                case "id": if (f.FieldType == typeof(string)) f.SetValue(box, "spire_codex_stats"); break;
                case "issmart":
                case "isdebuff":
                case "isinstanced":
                case "shouldoverridetextoverflow":
                    if (f.FieldType == typeof(bool)) f.SetValue(box, false); break;
            }
        }
        return box;
    }

    private static bool ContainsOurTip(object? tips)
    {
        if (tips is not IEnumerable seq) return false;
        foreach (var item in seq)
            if (item != null && Reflect.GetString(item, "Id") == "spire_codex_stats") return true;
        return false;
    }

    private static object Append(object? existing, object tip)
    {
        var listType = typeof(List<>).MakeGenericType(_iHoverTipType!);
        var list = (IList)Activator.CreateInstance(listType)!;
        if (existing is IEnumerable seq)
            foreach (var item in seq) list.Add(item);
        list.Add(tip);
        return list;
    }

    private static string Strip(string field)
    {
        var s = field;
        if (s.Length > 1 && s[0] == '<')
        {
            var gt = s.IndexOf('>');
            if (gt > 1) s = s.Substring(1, gt - 1);
        }
        return s.ToLowerInvariant();
    }

    private static string? Bare(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var s = raw;
        var sp = s.IndexOf(' '); if (sp >= 0) s = s.Substring(0, sp);
        var dot = s.IndexOf('.'); if (dot >= 0) s = s.Substring(dot + 1);
        return s;
    }

    private static string TierHex(string tier) => tier switch
    {
        "S" => "#ffd34d", "A" => "#86e08a", "B" => "#6bd3c7",
        "C" => "#e8e3d6", "D" => "#e0b070", _ => "#e08a86",
    };

    private static string Pretty(string id)
    {
        var parts = id.Split('_');
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0)
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1).ToLowerInvariant();
        return string.Join(" ", parts);
    }

    private static object? ResolveCardModel(object owner)
    {
        if (_cardHolderType?.IsInstanceOfType(owner) == true)
        {
            var m0 = Reflect.GetMember(owner, "CardModel");
            if (IsModelType(m0, "CardModel")) return m0;
        }
        var node = Reflect.GetMember(owner, "_cardNode") ?? Reflect.GetMember(owner, "CardNode")
            ?? Reflect.GetMember(owner, "_card") ?? Reflect.GetMember(owner, "Card");
        var m = Reflect.GetMember(node, "Model");
        if (IsModelType(m, "CardModel")) return m;
        if (IsModelType(node, "CardModel")) return node;
        m = Reflect.GetMember(owner, "CardModel");
        if (IsModelType(m, "CardModel")) return m;
        return null;
    }

    private static object? ResolveRelicModel(object owner)
    {
        var node = _relicType?.IsInstanceOfType(owner) == true
            ? owner
            : Reflect.GetMember(owner, "Relic") ?? Reflect.GetMember(owner, "_relic");
        var m = Reflect.GetMember(node, "Model");
        if (IsModelType(m, "RelicModel")) return m;
        if (IsModelType(node, "RelicModel")) return node;

        m = Reflect.GetMember(owner, "_model") ?? Reflect.GetMember(owner, "Model");
        if (IsModelType(m, "RelicModel")) return m;

        var reward = Reflect.GetMember(owner, "Reward");
        m = Reflect.GetMember(reward, "Relic") ?? Reflect.GetMember(reward, "_relic");
        if (IsModelType(m, "RelicModel")) return m;

        if (Reflect.GetMember(owner, "_relics") is IList relics)
        {
            var idx = Reflect.GetInt(owner, "_index", -1);
            if (idx >= 0 && idx < relics.Count && IsModelType(relics[idx], "RelicModel")) return relics[idx];
        }
        return null;
    }

    private static object? ResolvePotionModel(object owner)
    {
        var direct = Reflect.GetMember(owner, "_potion") ?? Reflect.GetMember(owner, "PotionModel");
        if (IsModelType(direct, "PotionModel")) return direct;
        var node = Reflect.GetMember(owner, "Potion") ?? Reflect.GetMember(owner, "_potionNode")
            ?? Reflect.GetMember(owner, "PotionNode");
        if (IsModelType(node, "PotionModel")) return node;
        var m = Reflect.GetMember(node, "Model");
        if (IsModelType(m, "PotionModel")) return m;
        return null;
    }

    private static bool IsModelType(object? o, string typeName)
    {
        for (var t = o?.GetType(); t != null; t = t.BaseType)
            if (t.Name == typeName) return true;
        return false;
    }

    private static readonly HashSet<string> SeenOwners = new();

    private static void DiagOwnerOnce(object owner)
    {
        var name = owner.GetType().Name;
        if (SeenOwners.Add(name)) Diag($"unresolved hover owner: {name}");
    }

    private static void Diag(string msg)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "spire-codex-cardhints.log"),
                $"{DateTimeOffset.UtcNow:o}  [native-tip] {msg}\n");
        }
        catch {  }
        MainFile.Logger.Info($"native-tip: {msg}");
    }
}
