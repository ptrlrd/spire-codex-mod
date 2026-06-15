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

// Reuses the game's own hover-tip widget for our card-reward stats, so they look native
// instead of a bolted-on panel.
//
// When a card holder is hovered the game already does:
//     NHoverTipSet.CreateAndShow(this, CardNode.Model.HoverTips);
//     set.SetAlignmentForCardHolder(this);
// We Harmony-prefix CreateAndShow and append our own HoverTip (a "Spire Codex" stat block)
// to the list. The game then renders and positions it inside its own tooltip set, with the
// same scene, font, panel and side-aware alignment. No custom positioning, fully native.
//
// All MegaCrit type access is reflection and isolated here in Core, and every path is
// guarded so a renamed type just disables the feature instead of breaking the game's tips.
internal static class NativeHoverTips
{
    private static Type? _hoverTipType;   // MegaCrit.Sts2.Core.HoverTips.HoverTip (struct)
    private static Type? _iHoverTipType;  // MegaCrit.Sts2.Core.HoverTips.IHoverTip
    private static Type? _cardHolderType; // MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder
    private static Type? _relicType;      // MegaCrit.Sts2.Core.Nodes.Relics.NRelic
    private static Type? _hoverTipSetType;// MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet
    private static Type? _portraitTipType;// NTopBarPortraitTip (note: lowercase "sts2" namespace)
    private static Type? _restButtonType; // NRestSiteButton (campfire options; no game tip)
    private static MethodBase? _createAndShowSingle; // CreateAndShow(Control, IHoverTip, alignment)
    private static MethodBase? _hoverTipRemove;      // NHoverTipSet.Remove(Control)
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

            // The single-tip overload, used to show our own portrait/campfire tips, and
            // the by-owner Remove used to clear them on unfocus.
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

    // At Ascension 0 the portrait shows NO game tip at all (ShowTip is false and the control
    // is not focusable), so there is nothing to append to. Patch the portrait control: make
    // it hoverable, and on focus show our character-stats tip when the game shows none.
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

    // Campfire option buttons show NO game tooltip at all (OnFocus is pure visuals), so we
    // create our own: OnFocus postfix shows the community campfire stats; the unfocus side
    // rides NClickableControl.OnUnfocus (NRestSiteButton doesn't override it) with a type
    // guard so only campfire buttons are touched.
    private static void ApplyRestSitePatch(Harmony harmony)
    {
        try
        {
            if (_restButtonType == null || _createAndShowSingle == null || _hoverTipRemove == null)
            {
                Diag("rest-site types not found; campfire stats disabled");
                return;
            }
            // Patch the MOST-DERIVED declarations: Harmony hooks a method declaration, not
            // the virtual slot, so patching a base method misses subclass overrides (the
            // original cause of stuck campfire tips - NRestSiteButton overrides OnUnfocus).
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

    // Walk down from the type to its bases and return the first DECLARED method, i.e. the
    // override that virtual dispatch would actually run.
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
            // Clear any tip still tracked for this owner first: a stale entry makes the
            // game's CreateAndShow throw after parenting the new set, orphaning it on
            // screen forever (the "tips stay up" bug).
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
        catch { /* removal is best-effort */ }
    }

    // "Smith — picked 54% of campfires / At your HP: 61% / Win rate when chosen 52%".
    // The action id falls out of the option's type name (SmithRestSiteOption -> SMITH).
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
        sb.Append($"[b]{rc.Label}[/b] — picked [b]{rc.Pct:0}%[/b] of campfires\n");
        var hp = RewardContext.HpPct;
        if (hp is { } h && rc.PctLowHp is { } lo && rc.PctHighHp is { } hi)
        {
            var band = h < 50 ? lo : hi;
            sb.Append($"At your HP ({h:0}%): players pick this [b]{band:0}%[/b]\n");
        }
        if (rc.WinRate is { } wr)
            sb.Append($"Win rate when chosen [b]{wr:0.0}%[/b]\n");
        return sb.ToString().TrimEnd();
    }

    private static void PortraitInitPostfix(object __instance)
    {
        try
        {
            if (!SpireCodexConfig.ShowHoverTips) return;
            // The game disables focus when it has no ascension tip; re-enable so hover fires.
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
            if (Reflect.GetBool(__instance, "ShowTip")) return; // game tip shown; Prefix appended ours
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
    }

    // NHoverTipSet.CreateAndShow(Control, IEnumerable<IHoverTip>, HoverTipAlignment)
    private static MethodBase? FindCreateAndShow()
    {
        if (_hoverTipSetType == null || _iHoverTipType == null) return null;
        var enumerableOfTip = typeof(IEnumerable<>).MakeGenericType(_iHoverTipType);
        return _hoverTipSetType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "CreateAndShow"
                && m.GetParameters().Length == 3
                && m.GetParameters()[1].ParameterType == enumerableOfTip);
    }

    // "Spire" gold, "Codex" white — the tip's first line (the native Title label can't
    // do mixed colors, so the tip's Title stays null and this leads the description).
    private const string Logo = "[color=#ffd34d][b]Spire[/b][/color] [color=#ffffff][b]Codex[/b][/color]";

    // Append our stat tip to the hover-tip list the game is about to render.
    private static void Prefix(object[] __args)
    {
        try
        {
            if (_iHoverTipType == null || __args.Length < 2) return;
            if (!SpireCodexConfig.ShowHoverTips) return;
            var owner = __args[0];
            if (owner == null) return;
            if (ContainsOurTip(__args[1])) return; // we initiated this call; don't double-append

            var text = BuildTipText(owner);
            if (text == null) return;
            var tip = BuildTip(null, text);
            if (tip == null) return;

            __args[1] = Append(__args[1], tip);
        }
        catch (Exception e) { Diag($"prefix error: {e.GetType().Name}: {e.Message}"); }
    }

    // Route the hover owner to the right tip: card / relic / potion stats, the shop's
    // card-removal service, or the top-bar character portrait. Null -> add nothing.
    private static string? BuildTipText(object owner)
    {
        var model = ResolveCardModel(owner);
        if (model != null)
        {
            var id = Bare(Reflect.GetString(model, "Id"));
            if (id == null || CodexScores.Card(id) is not { Picks: > 0 } sc) return null;
            return BuildStats(sc, CardStatsCache.Get(id), id == RewardContext.BestCardId, showElo: true);
        }

        model = ResolveRelicModel(owner);
        if (model != null)
        {
            var id = Bare(Reflect.GetString(model, "Id"));
            if (id == null || CodexScores.Relic(id) is not { Picks: > 0 } sc) return null;
            var text = BuildStats(sc, RelicStatsCache.Get(id), isBest: false, showElo: false);
            // How coveted this relic is at Ancient 3-relic offers (the informed-decision
            // line for the ancient screen; harmless context elsewhere).
            if (CommunityStats.Ancient(id) is { } anc)
                text += $"\nAncient offers: taken [b]{anc.TakeRate:0}%[/b]  [color=#9aa3ab]({anc.Offered:N0} offers)[/color]";
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

        DiagOwnerOnce(owner); // surface unknown hover owners instead of failing silently
        return null;
    }

    // The compact stat block:
    //   Spire Codex
    //   Best pick            (only when flagged)
    //   S tier
    //   Codex Elo 1821       (cards with an Elo only)
    //   Codex Score 71
    //   Win rate 63.9% (+3.2% vs base)   <- current character's number when known
    //   Pick rate 5.0%
    private static string BuildStats(EntityScore sc, CardStats? full, bool isBest, bool showElo)
    {
        // Elo-rated cards use the Elo tier (matches the plate); everything else the Score tier.
        var tier = CodexScores.EloTier(sc.Elo) ?? Ranks.Tier(sc.Score);
        var character = RewardContext.Character;
        var sb = new StringBuilder();
        sb.Append(Logo).Append('\n');
        if (isBest) sb.Append("[color=#ffd34d][b]Best pick[/b][/color]\n");
        sb.Append($"[color={TierHex(tier)}][b]{tier} tier[/b][/color]\n");
        if (showElo && sc.Elo is { } elo) sb.Append($"Codex Elo [b]{elo:0}[/b]\n");
        sb.Append($"Codex Score [b]{sc.Score:0}[/b]\n");

        // Win rate: current character's slice when available, else global.
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

        sb.Append($"Win rate [b]{wr:0.0}%[/b]");
        if (delta is { } d)
        {
            var dc = d >= 0 ? "#86e08a" : "#e08a86";
            sb.Append($"  [color={dc}]({(d >= 0 ? "+" : "")}{d:0.0}% vs base)[/color]");
        }
        sb.Append('\n');

        if (full is { PickRate: > 0 }) sb.Append($"Pick rate [b]{full.PickRate:0.0}%[/b]\n");
        return sb.ToString().TrimEnd();
    }

    // Event option: how the community decides this event. The button exposes Event
    // (EventModel) and Option (EventOption.TextKey); community option ids are the upper-cased
    // key, with staged repeats suffixed KEY_0, KEY_1... which we sum into one number.
    private static readonly HashSet<string> EventDiagSeen = new();

    private static string? BuildEventOptionText(object owner)
    {
        CommunityStats.EnsureLoaded();
        if (CommunityStats.Data == null) { EventDiag("(any)", "community stats not loaded yet"); return null; }
        var evId = Bare(Reflect.GetString(Reflect.GetMember(owner, "Event"), "Id"));
        // TextKey is the full loc path ("SLIPPERY_BRIDGE.pages.INITIAL.options.OVERCOME");
        // the community option id is its last segment ("OVERCOME", "HOLD_ON_1", ...).
        var rawKey = Reflect.GetString(Reflect.GetMember(owner, "Option"), "TextKey");
        var key = rawKey?.Substring(rawKey.LastIndexOf('.') + 1).ToUpperInvariant();
        if (evId == null || string.IsNullOrEmpty(key))
        {
            EventDiag(evId ?? "(no-event-id)", $"missing identifiers (key={key ?? "null"})");
            return null;
        }
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
        sb.Append($"[b]{pct:0}%[/b] of players pick this  [color=#9aa3ab]({count:N0} of {ev.Total:N0})[/color]");
        return sb.ToString();
    }

    // One line per distinct event-tip miss, so a missing percentage is never silent.
    private static void EventDiag(string evId, string why)
    {
        if (EventDiagSeen.Add($"{evId}|{why}")) Diag($"event tip miss: {evId}: {why}");
    }

    // Shop card-removal service: what the community actually removes.
    private static string? BuildRemovalText()
    {
        CommunityStats.EnsureLoaded();
        var data = CommunityStats.Data;
        if (data == null || data.MostRemoved.Count == 0) return null;
        var sb = new StringBuilder();
        sb.Append(Logo).Append('\n');
        sb.Append("[b]Most removed by the community[/b]\n");
        var n = Math.Min(3, data.MostRemoved.Count);
        for (var i = 0; i < n; i++)
        {
            var r = data.MostRemoved[i];
            sb.Append($"{i + 1}. {r.Name}  [color=#9aa3ab]{r.Pct:0.0}% of removals[/color]\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static bool _portraitDiagged;

    // Top-bar character portrait: your win rate (local run history) + the community's.
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
        sb.Append($"[b]{Pretty(character)}[/b]\n");
        if (mine != null)
            sb.Append($"Your win rate [b]{mine.WinRate:0.0}%[/b]  [color=#9aa3ab]({mine.Runs:N0} runs)[/color]\n");
        if (community != null)
            sb.Append($"Community [b]{community.WinRate:0.0}%[/b]  [color=#9aa3ab]({community.Runs:N0} runs · {community.Share:0.0}% of all)[/color]\n");
        return sb.ToString().TrimEnd();
    }

    // Build a boxed HoverTip struct directly via reflection (its public ctors all need a
    // LocString; we only need Title/Description/Id, which are plain CLR fields). A null
    // title hides the tip's title row (our logo line leads the description instead).
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

    // Copy the incoming tips into a fresh List<IHoverTip> and add ours, keeping the
    // argument's static type (IEnumerable<IHoverTip>) intact for Harmony's write-back.
    private static object Append(object? existing, object tip)
    {
        var listType = typeof(List<>).MakeGenericType(_iHoverTipType!);
        var list = (IList)Activator.CreateInstance(listType)!;
        if (existing is IEnumerable seq)
            foreach (var item in seq) list.Add(item);
        list.Add(tip);
        return list;
    }

    // "<Title>k__BackingField" -> "title"; "Title" -> "title"
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

    // Card surfaces: NCardHolder family (reward rows, pack previews) expose CardModel
    // directly; the shop's NMerchantCard holds an NCard in _cardNode. Card models are
    // concrete subclasses, so checks walk the base-type chain.
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
        if (IsModelType(node, "CardModel")) return node; // the member was already the model
        m = Reflect.GetMember(owner, "CardModel");
        if (IsModelType(m, "CardModel")) return m;
        return null;
    }

    // Relic surfaces pass different owners to CreateAndShow:
    //   - NRelicBasicHolder / NRelicInventoryHolder (top bar, OnFocus): owner.Relic -> NRelic -> Model
    //   - shop/treasure holders: same Relic/_relic shape
    //   - NRewardButton (rewards list): owner.Reward (RelicReward) -> Relic/_relic
    //   - NInspectRelicScreen: owner._relics[_index]
    // Relics are concrete subclasses (344 of them, e.g. Akabeko), so the check must walk the
    // base-type chain for RelicModel, never compare the runtime type name directly.
    private static object? ResolveRelicModel(object owner)
    {
        var node = _relicType?.IsInstanceOfType(owner) == true
            ? owner
            : Reflect.GetMember(owner, "Relic") ?? Reflect.GetMember(owner, "_relic");
        var m = Reflect.GetMember(node, "Model");
        if (IsModelType(m, "RelicModel")) return m;
        if (IsModelType(node, "RelicModel")) return node; // the Relic member was already the model

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

    // Potion surfaces: NPotionHolder (belt) exposes Potion (NPotion) -> Model; the shop's
    // NMerchantPotion holds the PotionModel directly in _potion (and an NPotion in _potionNode).
    private static object? ResolvePotionModel(object owner)
    {
        var direct = Reflect.GetMember(owner, "_potion") ?? Reflect.GetMember(owner, "PotionModel");
        if (IsModelType(direct, "PotionModel")) return direct;
        var node = Reflect.GetMember(owner, "Potion") ?? Reflect.GetMember(owner, "_potionNode")
            ?? Reflect.GetMember(owner, "PotionNode");
        if (IsModelType(node, "PotionModel")) return node; // the member was already the model
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

    // Log each unrecognized hover-tip owner type once, so new surfaces (potions, shop items,
    // compendium entries) show up in the log instead of silently not matching.
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
        catch { /* ignore */ }
        MainFile.Logger.Info($"native-tip: {msg}");
    }
}
