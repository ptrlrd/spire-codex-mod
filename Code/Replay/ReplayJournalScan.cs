using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SpireCodex.Replay;

public static class ReplayJournalScan
{
    public static long LastSequence(string path)
    {
        try
        {
            if (!File.Exists(path)) return -1;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var take = (int)Math.Min(fs.Length, 8192);
            if (take == 0) return -1;
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            var read = fs.Read(buf, 0, take);
            var tail = System.Text.Encoding.UTF8.GetString(buf, 0, read);

            var best = -1L;
            foreach (var line in tail.Split('\n'))
                best = MaxField(line, "s", best);
            return best;
        }
        catch { return -1; }
    }

    public readonly struct HighWater
    {
        public HighWater(long seq, int card, int decision, int creature, string? deckLine)
        {
            Seq = seq;
            Card = card;
            Decision = decision;
            Creature = creature;
            DeckLine = deckLine;
        }

        public long Seq { get; }
        public int Card { get; }
        public int Decision { get; }

        public int Creature { get; }

        public string? DeckLine { get; }
    }

    public static HighWater HighWaterOf(string path)
    {
        var seq = -1L;
        var card = 0L;
        var decision = 0L;
        var creature = 0L;
        string? deckLine = null;
        try
        {
            if (!File.Exists(path)) return new HighWater(-1, 0, 0, 0, null);
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                seq = MaxField(line, "s", seq);
                card = MaxField(line, "c", card);
                card = MaxField(line, "deck_c", card);
                card = MaxField(line, "from_c", card);
                card = MaxField(line, "to_c", card);
                card = MaxField(line, "instance_id", card);
                card = MaxArray(line, "order_c", card);
                card = MaxArray(line, "order_deck_c", card);
                card = MaxArray(line, "flushed_c", card);
                card = MaxArray(line, "retained_c", card);
                decision = MaxField(line, "decision_id", decision);
                creature = MaxField(line, "cid", creature);
                creature = MaxField(line, "target_cid", creature);
                creature = MaxField(line, "src_cid", creature);
                creature = MaxField(line, "dst_cid", creature);
                creature = MaxField(line, "tgt_cid", creature);
                if (line.Contains("\"t\":\"deck\"", StringComparison.Ordinal)
                    || line.Contains("\"starting_deck\":", StringComparison.Ordinal))
                    deckLine = line;
            }
        }
        catch
        {
        }
        return new HighWater(seq, (int)card, (int)decision, (int)creature, deckLine);
    }

    private static long MaxField(string line, string key, long best)
    {
        var needle = "\"" + key + "\":";
        var at = line.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            var start = at + needle.Length;
            while (start < line.Length && char.IsWhiteSpace(line[start])) start++;
            var end = start;
            while (end < line.Length && char.IsDigit(line[end])) end++;
            if (end > start && long.TryParse(line.Substring(start, end - start), out var v) && v > best)
                best = v;
            at = line.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }
        return best;
    }

    private static long MaxArray(string line, string key, long best)
    {
        var needle = "\"" + key + "\":[";
        var at = line.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            var i = at + needle.Length;
            while (i < line.Length && line[i] != ']')
            {
                var start = i;
                while (i < line.Length && char.IsDigit(line[i])) i++;
                if (i > start && long.TryParse(line.Substring(start, i - start), out var v) && v > best)
                    best = v;
                if (i == start) i++;
            }
            at = line.IndexOf(needle, i, StringComparison.Ordinal);
        }
        return best;
    }

    public readonly record struct CompetitiveHistory(bool StartedOn, string? Flag, int CrashRecoveries, int UnexplainedExits);

    private static readonly string[] CrashMarks =
    {
        "Program crashed", "Dumping the backtrace", "CrashHandlerException", "Unhandled exception",
        "FATAL:", "Fatal error", "Out of memory", "Parameter \"mem\" is null",
    };

    private static readonly string[] CleanMarks =
    {
        "leaked at exit", "still in use at exit",
    };

    public static string ExitKind(string logTail)
    {
        foreach (var mark in CrashMarks)
            if (logTail.Contains(mark, StringComparison.OrdinalIgnoreCase)) return "crash";
        foreach (var mark in CleanMarks)
            if (logTail.Contains(mark, StringComparison.Ordinal)) return "clean";
        return "killed";
    }

    public static CompetitiveHistory CompetitiveOf(string path)
    {
        var headers = 0;
        var startedOn = false;
        string? flag = null;
        var crashes = 0;
        var unexplained = 0;
        string? open = null;
        var quit = false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var header = line.Contains("\"t\":\"header\"", StringComparison.Ordinal);
                var end = line.Contains("\"t\":\"end\"", StringComparison.Ordinal);
                var mark = line.Contains("\"t\":\"competitive\"", StringComparison.Ordinal);
                var starts = line.Contains("\"t\":\"combat_start\"", StringComparison.Ordinal);
                var ends = line.Contains("\"t\":\"combat_end\"", StringComparison.Ordinal);
                if (!header && !end && !mark && !starts && !ends) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (starts) open = Text(root, "combat_id");
                    else if (ends) open = null;
                    else if (header)
                    {
                        headers++;
                        var on = root.TryGetProperty("competitive", out var c) && c.ValueKind == JsonValueKind.True;
                        if (headers == 1) startedOn = on;
                        else if (!on && startedOn) flag ??= "toggled";
                        flag ??= Text(root, "competitive_flag");
                    }
                    else if (mark)
                    {
                        flag ??= Text(root, "flag");
                        if (Text(root, "event") == "quit") quit = true;
                    }
                    else
                    {
                        var reason = Text(root, "terminal_reason");
                        if (reason == "left_run") flag ??= open != null ? "reload" : "quit";
                        else if (reason == "interrupted")
                        {
                            var exit = Text(root, "exit");
                            if (quit || exit == "clean") flag ??= open != null ? "reload" : "quit";
                            else if (open != null && exit == "killed")
                            {
                                unexplained++;
                                if (unexplained > 1) flag ??= "killed";
                            }
                            else if (exit != "clean") crashes++;
                        }
                        quit = false;
                        open = null;
                    }
                }
                catch { }
            }
        }
        catch { }
        return new CompetitiveHistory(startedOn, flag, crashes, unexplained);
    }

    public readonly record struct Progress(int MaxFloor, int MaxReloads, HashSet<string> EndedCombats);

    public static Progress ProgressOf(string path)
    {
        var floor = -1;
        var reloads = -1;
        var ended = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                floor = Math.Max(floor, TopFloor(line));
                var ends = line.Contains("\"t\":\"combat_end\"", StringComparison.Ordinal);
                var counts = line.Contains("\"t\":\"header\"", StringComparison.Ordinal)
                    || line.Contains("\"t\":\"resume\"", StringComparison.Ordinal);
                if (!ends && !counts) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (ends)
                    {
                        if (Text(root, "combat_id") is { } id) ended.Add(id);
                    }
                    else if (root.TryGetProperty("reloads", out var r) && r.ValueKind == JsonValueKind.Number)
                        reloads = Math.Max(reloads, r.GetInt32());
                }
                catch { }
            }
        }
        catch { }
        return new Progress(floor, reloads, ended);
    }

    public static bool RolledBack(Progress seen, int floor, int reloads)
        => (floor > 0 && floor < seen.MaxFloor) || reloads < seen.MaxReloads;

    public static string? Bracket(string? gameMode, int ascension, int players, int modifiers)
    {
        if (players != 1) return null;
        if (string.Equals(gameMode, "Dread", StringComparison.OrdinalIgnoreCase)) return "dread_solo";
        if (string.Equals(gameMode, "Standard", StringComparison.OrdinalIgnoreCase)
            && ascension == 10 && modifiers == 0)
            return "a10_solo";
        return null;
    }

    private static int TopFloor(string line)
    {
        const string needle = "\"floor\":";
        var at = line.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return -1;
        var nested = line.IndexOfAny(new[] { '{', '[' }, 1);
        if (nested >= 0 && nested < at) return -1;
        var upTo = Math.Min(line.Length, at + needle.Length + 12);
        return (int)MaxField(line.Substring(0, upTo), "floor", -1);
    }

    public static string? OpenCombat(string path)
    {
        string? open = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var starts = line.Contains("\"t\":\"combat_start\"", StringComparison.Ordinal);
                if (!starts && !line.Contains("\"t\":\"combat_end\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    open = starts ? Text(doc.RootElement, "combat_id") : null;
                }
                catch { }
            }
        }
        catch { return null; }
        return open;
    }

    public static List<DeckRemap.Entry> DeckEntries(string? line)
    {
        var rows = new List<DeckRemap.Entry>();
        if (string.IsNullOrEmpty(line)) return rows;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var kind = Text(doc.RootElement, "t");
            if (kind != "deck" && kind != "header") return rows;
            if (!doc.RootElement.TryGetProperty("cards", out var cards)
                && !doc.RootElement.TryGetProperty("starting_deck", out cards))
                return rows;
            if (cards.ValueKind != JsonValueKind.Array) return rows;
            foreach (var card in cards.EnumerateArray())
            {
                if (card.ValueKind != JsonValueKind.Object) return Empty(rows);
                if (!card.TryGetProperty("c", out var c) || c.ValueKind != JsonValueKind.Number)
                    return Empty(rows);
                if (!card.TryGetProperty("up", out var up) || up.ValueKind != JsonValueKind.Number)
                    return Empty(rows);
                rows.Add(new DeckRemap.Entry(c.GetInt32(), Key(
                    Text(card, "id"), up.GetInt32(), Text(card, "enchantment"),
                    card.TryGetProperty("amount", out var amt)
                        && amt.ValueKind == JsonValueKind.Number ? amt.GetInt32() : 0),
                    Tag(card.TryGetProperty("added_floor", out var floor)
                        && floor.ValueKind == JsonValueKind.Number ? floor.GetInt32() : (int?)null)));
            }
        }
        catch { return Empty(rows); }
        return rows;
    }

    public static string Key(string? id, int up, string? enchantment, int amount)
        => $"{id}|{up}|{enchantment}|{amount}";

    public static string? Tag(int? addedFloor)
        => addedFloor == null
            ? null
            : "f" + addedFloor.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static List<DeckRemap.Entry> Empty(List<DeckRemap.Entry> rows)
    {
        rows.Clear();
        return rows;
    }

    private static string? Text(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
