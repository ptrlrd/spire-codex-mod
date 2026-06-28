using System;
using System.Reflection;

namespace SpireCodex.Core;

// Tiny reflection helpers. All game-type access goes through reflection so the mod
// compiles and degrades gracefully even when an Early Access patch renames a member.
// A missing member returns null/fallback and the caller downgrades the snapshot
// instead of crashing the game.
internal static class Reflect
{
    private const BindingFlags Flags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static object? GetMember(object? target, string name)
    {
        if (target == null) return null;
        // Guarded: some game getters throw when read too early (e.g. NPotion.Model before
        // the model is set). A throwing member reads as absent, same as a missing one.
        try
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name, Flags);
                if (p != null) return p.GetValue(target);
                var f = t.GetField(name, Flags);
                if (f != null) return f.GetValue(target);
            }
        }
        catch
        {
            // fall through to null
        }
        return null;
    }

    // Read a public/non-public STATIC property or field by name off a Type. Same graceful
    // contract: a missing or throwing member reads as null. Used for game singletons accessed
    // without a hard type reference (e.g. RunManager.Instance).
    public static object? GetStatic(Type? type, string name)
    {
        if (type == null) return null;
        try
        {
            var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (p != null) return p.GetValue(null);
            var f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f != null) return f.GetValue(null);
        }
        catch { /* fall through to null */ }
        return null;
    }

    public static int GetInt(object? target, string name, int fallback = 0)
    {
        var v = GetMember(target, name);
        if (v == null) return fallback;
        try { return Convert.ToInt32(v); }
        catch { return fallback; }
    }

    public static bool GetBool(object? target, string name, bool fallback = false)
        => GetMember(target, name) is bool b ? b : fallback;

    public static string? GetString(object? target, string name)
        => GetMember(target, name)?.ToString();

    // Invoke a parameterless instance method and return its result as a string. Needed for
    // types whose ToString() is unhelpful (e.g. LocString, which resolves its localized
    // text only via GetFormattedText()). Null on any failure, same graceful contract.
    public static string? CallString(object? target, string method)
    {
        if (target == null) return null;
        try
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var m = t.GetMethod(method, Flags, null, Type.EmptyTypes, null);
                if (m != null) return m.Invoke(target, null)?.ToString();
            }
        }
        catch { /* fall through to null */ }
        return null;
    }

    // Invoke a parameterless instance method and return its object result (e.g.
    // MerchantRoom.GetLocalInventory()). Null on any failure, same graceful contract.
    public static object? Call(object? target, string method)
    {
        if (target == null) return null;
        try
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var m = t.GetMethod(method, Flags, null, Type.EmptyTypes, null);
                if (m != null) return m.Invoke(target, null);
            }
        }
        catch { /* fall through to null */ }
        return null;
    }

    // Invoke a single-argument instance method by name and return its object result (e.g.
    // EncounterModel.GetLossMessageFor(character)). Matches by name + arity. Null on any failure.
    public static object? CallWith(object? target, string method, object? arg)
    {
        if (target == null) return null;
        try
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
                foreach (var m in t.GetMethods(Flags))
                    if (m.Name == method && m.GetParameters().Length == 1)
                        return m.Invoke(target, new[] { arg });
        }
        catch { /* fall through to null */ }
        return null;
    }
}
