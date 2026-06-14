using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using Godot;

namespace SpireCodex.Core;

// One-shot reflection dumps of live game objects so we can learn the exact member
// names to read. Mirrors the compendium's DumpSurfacesOnce. Two dumps:
//   - general (RunState/Player/deck/card/relic/potion), as soon as the deck has cards
//   - combat  (PlayerCombatState/CombatRoom/enemy), as soon as a fight starts
// Both gated, write once per launch, then no-op. Discovery tooling, gated behind
// Config.DumpIntrospection. Delete once everything needed is wired into Sts2Access.
internal static class Introspect
{
    private static bool _generalDone;
    private static bool _combatDone;

    private const BindingFlags Flags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    public static void DumpOnce(object runState, object player)
    {
        DumpGeneral(runState, player);
        DumpCombat(runState, player);
    }

    // On-demand dump of arbitrary live objects to a file (triggered by a hotkey).
    public static void DumpToFile(string fileName, params (string Label, object? Obj)[] items)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# spire-codex on-demand dump @ {DateTimeOffset.UtcNow:o}");
        foreach (var (label, obj) in items) DumpType(sb, label, obj);
        Write(sb, fileName);
    }

    private static void DumpGeneral(object runState, object player)
    {
        if (_generalDone) return;
        var deck = Reflect.GetMember(player, "Deck");
        var firstCard = FirstElement(deck);
        if (firstCard == null) return; // wait until the deck has cards
        _generalDone = true;

        var sb = new StringBuilder();
        sb.AppendLine($"# spire-codex general introspection @ {DateTimeOffset.UtcNow:o}");
        DumpType(sb, "RunState", runState);
        DumpType(sb, "Player", player);
        DumpType(sb, "Deck (container)", deck);
        DumpType(sb, "Card[0]", firstCard);
        DumpType(sb, "Relic[0]", FirstElement(Reflect.GetMember(player, "Relics")));
        DumpType(sb, "Potion[0]", FirstElement(Reflect.GetMember(player, "Potions")));
        Write(sb, "spire-codex-introspect.txt");
    }

    private static void DumpCombat(object runState, object player)
    {
        if (_combatDone) return;
        var pcs = Reflect.GetMember(player, "PlayerCombatState");
        if (pcs == null) return; // not in a fight yet
        _combatDone = true;

        var sb = new StringBuilder();
        sb.AppendLine($"# spire-codex combat introspection @ {DateTimeOffset.UtcNow:o}");
        DumpType(sb, "PlayerCombatState (energy/block live here)", pcs);
        var room = Reflect.GetMember(runState, "CurrentRoom");
        DumpType(sb, "CurrentRoom", room);
        DumpType(sb, "CombatState (turn/intents?)", Reflect.GetMember(room, "CombatState"));
        var enemy = FindFirstCreature(room) ?? FindFirstCreature(pcs) ?? FindFirstCreature(runState);
        DumpType(sb, "Enemy[0]", enemy);
        var monster = Reflect.GetMember(enemy, "Monster");
        DumpType(sb, "Enemy[0].Monster (intent?)", monster);
        DumpType(sb, "Enemy[0].Monster.NextMove (MoveState - damage/hits)", Reflect.GetMember(monster, "NextMove"));
        Write(sb, "spire-codex-introspect-combat.txt");
    }

    // Try common collection member names that would hold enemy creatures.
    private static object? FindFirstCreature(object? container)
    {
        if (container == null) return null;
        foreach (var name in new[] { "Monsters", "Enemies", "Creatures", "Opponents", "Combatants", "Targets" })
        {
            if (Reflect.GetMember(container, name) is IEnumerable seq)
            {
                foreach (var item in seq)
                {
                    if (item != null) return item;
                }
            }
        }
        return null;
    }

    private static object? FirstElement(object? container)
    {
        if (container == null) return null;
        var seq = Reflect.GetMember(container, "Cards") as IEnumerable ?? container as IEnumerable;
        if (seq == null) return null;
        foreach (var item in seq) return item;
        return null;
    }

    private static void DumpType(StringBuilder sb, string label, object? obj)
    {
        sb.AppendLine();
        sb.AppendLine($"== {label} ==");
        if (obj == null) { sb.AppendLine("(null)"); return; }

        var t = obj.GetType();
        sb.AppendLine($"type: {t.FullName}");
        sb.AppendLine($"ToString: {Safe(() => obj.ToString())}");

        for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
        {
            sb.AppendLine($"-- {cur.Name} --");
            foreach (var p in cur.GetProperties(Flags))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                sb.AppendLine($"  prop  {p.Name} : {p.PropertyType.Name} = {Safe(() => p.GetValue(obj))}");
            }
            foreach (var f in cur.GetFields(Flags))
            {
                sb.AppendLine($"  field {f.Name} : {f.FieldType.Name} = {Safe(() => f.GetValue(obj))}");
            }
        }
    }

    private static void Write(StringBuilder sb, string fileName)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), fileName);
            File.WriteAllText(path, sb.ToString());
            GD.Print($"[SpireCodex] introspection written to {path}");
        }
        catch (Exception e)
        {
            GD.Print($"[SpireCodex] introspect write failed: {e.Message}");
        }
    }

    private static string Safe(Func<object?> get)
    {
        try { return get()?.ToString() ?? "null"; }
        catch (Exception e) { return $"<err: {e.GetType().Name}>"; }
    }
}
