using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SpireCodex.Core;

namespace SpireCodex;

// Localizes the mod's OWN chrome (tabs, labels, prose, buttons). Game-data terms (card /
// relic / monster / event names) already come localized from the game via reflection, so
// they are never routed through here.
//
// Language comes from the game's LocManager.Instance.Language (a 3-letter code like "eng",
// "deu", "fra"). Read via reflection so a game-side rename degrades to English instead of
// crashing, matching the rest of the mod's game access (see Reflect).
//
// Translations live in an embedded JSON (Code/Loc/mod_ui.json), shape { lang: { key: text } }.
// Lookup falls back current-language -> English -> the key itself, so a partial translation
// degrades gracefully and a totally missing key is visibly the key (caught in dev).
internal static class Loc
{
    public const string Fallback = "eng";

    private static readonly Dictionary<string, Dictionary<string, string>> _tables = LoadTables();
    private static Type? _locManagerType;
    private static string? _lang; // cached once resolved to a real game language

    // Current 3-letter language code. Resolves lazily: at mod-load time the game's LocManager
    // is not initialized yet, so until it is we return English WITHOUT caching, and retry on
    // the next call. Once a real language resolves it is cached; Refresh() clears the cache.
    public static string Lang
    {
        get
        {
            if (_lang != null) return _lang;
            var resolved = Resolve();
            if (resolved != null) _lang = resolved;
            return resolved ?? Fallback;
        }
    }

    // Force the language to be re-read (call when the overlay opens, so switching language in
    // the game settings takes effect without a restart).
    public static void Refresh() => _lang = null;

    // Translate a key to the current language. Falls back to English, then to the key.
    public static string T(string key)
    {
        var lang = Lang;
        if (_tables.TryGetValue(lang, out var table) && table.TryGetValue(key, out var text))
            return text;
        if (lang != Fallback && _tables.TryGetValue(Fallback, out var eng) && eng.TryGetValue(key, out var engText))
            return engText;
        return key;
    }

    // Translate a format-template key and fill positional {0},{1}... placeholders. The template
    // owns word order per language; callers pass already-formatted value strings (so existing
    // number formatting like "0.0" is preserved). Invariant culture: values arrive pre-formatted.
    public static string F(string key, params object?[] args)
    {
        try { return string.Format(CultureInfo.InvariantCulture, T(key), args); }
        catch (FormatException) { return T(key); }
    }

    // Resolve a string from one of the GAME's own loc tables (e.g. table "characters", key
    // "NECROBINDER.title") so a mod-displayed game term uses the game's official translation
    // instead of a prettified id. Returns null on any failure (table/key missing, LocManager
    // not ready, member renamed) so callers can fall back.
    public static string? GameTerm(string table, string key)
    {
        try
        {
            _locManagerType ??= FindType("MegaCrit.Sts2.Core.Localization.LocManager");
            var instance = Reflect.GetStatic(_locManagerType, "Instance");
            if (instance == null) return null;
            var locTable = Reflect.CallWith(instance, "GetTable", table);
            if (locTable == null || Reflect.CallWith(locTable, "HasEntry", key) is not true) return null;
            return Reflect.CallWith(locTable, "GetRawText", key) as string;
        }
        catch { return null; }
    }

    // The game's localized display name for a content id, looked up in the matching game loc
    // table. Each returns null when unknown so a caller can fall back to a prettified id. The ids
    // the mod holds are the game's own upper-snake content ids (e.g. "ADAPTIVE_STRIKE"), which are
    // exactly the loc keys; we upper-case defensively in case a source ever lower-cases them.
    // Most tables key the name as "{ID}.title"; monsters use "{ID}.name".
    public static string? CharacterName(string? id) => Title("characters", id);
    public static string? CardName(string? id) => Title("cards", id);
    public static string? RelicName(string? id) => Title("relics", id);
    public static string? PotionName(string? id) => Title("potions", id);
    public static string? PowerName(string? id) => Title("powers", id);
    public static string? EnchantmentName(string? id) => Title("enchantments", id);
    public static string? EncounterName(string? id) => Title("encounters", id);
    public static string? MonsterName(string? id)
        => string.IsNullOrEmpty(id) ? null : GameTerm("monsters", id.ToUpperInvariant() + ".name");

    private static string? Title(string table, string? id)
        => string.IsNullOrEmpty(id) ? null : GameTerm(table, id.ToUpperInvariant() + ".title");

    private static string? Resolve()
    {
        try
        {
            _locManagerType ??= FindType("MegaCrit.Sts2.Core.Localization.LocManager");
            var instance = Reflect.GetStatic(_locManagerType, "Instance");
            if (instance == null) return null; // not initialized yet
            var lang = Reflect.GetString(instance, "Language");
            return string.IsNullOrEmpty(lang) ? null : lang;
        }
        catch { return null; }
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName, throwOnError: false);
            if (t != null) return t;
        }
        return null;
    }

    private static Dictionary<string, Dictionary<string, string>> LoadTables()
    {
        var empty = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        try
        {
            var asm = typeof(Loc).Assembly;
            string? resName = null;
            foreach (var n in asm.GetManifestResourceNames())
                if (n.EndsWith("mod_ui.json", StringComparison.Ordinal)) { resName = n; break; }
            if (resName == null) return empty;

            using var stream = asm.GetManifestResourceStream(resName);
            if (stream == null) return empty;
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json);
            return parsed ?? empty;
        }
        catch
        {
            return empty; // never let a bad resource break the mod; everything falls back to the key
        }
    }
}
