using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace SpireCodex.Core;

// Shared Harmony plumbing for patching the game's first-party hook points by name.
//
// Everything the mod reads from the game is reflection-only: we compile against no game
// types, so a method renamed by a game patch has to degrade to "that event stops firing"
// rather than a crash or a failed load. Every method here returns a count so callers can log
// "n/m patched" and notice a game update silently breaking capture.
//
// RunEvents.cs predates this and keeps its own copies; it is working code and was left alone
// rather than refactored as a side effect of adding the replay recorder.
internal static class HookPatcher
{
    // Patch a public static method on MegaCrit's Hook class by name.
    //
    // `method` may be a "|"-separated candidate list, tried in order. The game renames hooks
    // between versions and we compile against none of them: AfterTurnEnd exists in the v0.103
    // decompile but not in v0.111, which is why the turn-end line silently never appeared until
    // the "n/m patched" count was checked against the log.
    public static int Patch(Harmony harmony, Type? hook, string method, Type owner, string prefixName,
                            bool postfix = false)
    {
        if (hook == null) return 0;
        try
        {
            MethodInfo? target = null;
            foreach (var candidate in method.Split('|'))
            {
                target = hook.GetMethod(candidate, BindingFlags.Public | BindingFlags.Static);
                if (target != null) break;
            }
            if (target == null)
            {
                // Name the near-misses so the next game update says what to patch instead.
                var near = string.Join(", ", hook.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Select(m => m.Name)
                    .Where(n => method.Split('|').Any(c => n.Contains(c.Replace("After", "").Replace("Before", ""))))
                    .Distinct().Take(6));
                MainFile.Logger.Info($"hooks: {hook.Name}.{method} not found"
                                     + (near.Length > 0 ? $" (similar: {near})" : ""));
                return 0;
            }
            return Apply(harmony, target, owner, prefixName, postfix);
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"hooks: patching {method} failed: {e.Message}");
            return 0;
        }
    }

    // Patch a static OR instance method on any type, disambiguating overloads by parameter
    // count and optionally the first parameter's type name. GetMethod throws on overloads
    // (RelicCmd.Obtain, CardCmd.Upgrade), hence the manual scan. Generic definitions are
    // skipped: they need a constructed type Harmony can't infer here.
    public static int PatchOn(Harmony harmony, Type? target, string method, Type owner, string prefixName,
                              int paramCount, string? firstParamType = null, bool postfix = false)
    {
        if (target == null)
        {
            MainFile.Logger.Info($"hooks: type for {method} not found");
            return 0;
        }
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Static | BindingFlags.Instance;
            var m = target.GetMethods(flags).FirstOrDefault(
                x => x.Name == method && !x.IsGenericMethodDefinition
                     && x.GetParameters().Length == paramCount
                     && (firstParamType == null
                         || (paramCount > 0 && x.GetParameters()[0].ParameterType.Name == firstParamType)));
            if (m == null)
            {
                MainFile.Logger.Info($"hooks: {target.Name}.{method}({paramCount} args) not found");
                return 0;
            }
            return Apply(harmony, m, owner, prefixName, postfix);
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"hooks: patching {method} failed: {e.Message}");
            return 0;
        }
    }

    private static int Apply(Harmony harmony, MethodBase target, Type owner, string name, bool postfix)
    {
        var patch = owner.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        if (patch == null)
        {
            MainFile.Logger.Info($"hooks: patch method {owner.Name}.{name} missing");
            return 0;
        }
        var hm = new HarmonyMethod(patch);
        harmony.Patch(target, prefix: postfix ? null : hm, postfix: postfix ? hm : null);
        lock (Applied) Applied.Add(new AppliedPatch(harmony, target, patch, postfix));
        return 1;
    }

    // Every patch Apply installed, so Restore can tell when one stops being there.
    private sealed record AppliedPatch(Harmony Harmony, MethodBase Target, MethodInfo Patch, bool Postfix);

    private static readonly List<AppliedPatch> Applied = new();

    public sealed record LostPatch(string Method, string Patch, List<string> Owners);

    // Re-install any patch of ours that is no longer on its method, and say which. Patches
    // are only ever applied once at load, so a missing one means something else took it off
    // mid-session, and every row that hook writes has been silently absent since.
    public static List<LostPatch> Restore()
    {
        var lost = new List<LostPatch>();
        List<AppliedPatch> applied;
        lock (Applied) applied = Applied.ToList();
        foreach (var p in applied)
        {
            try
            {
                var info = Harmony.GetPatchInfo(p.Target);
                var present = info != null
                              && (p.Postfix ? info.Postfixes : info.Prefixes)
                                 .Any(x => x.owner == p.Harmony.Id && x.PatchMethod == p.Patch);
                if (present) continue;
                var owners = info?.Owners.ToList() ?? new List<string>();
                var hm = new HarmonyMethod(p.Patch);
                p.Harmony.Patch(p.Target, prefix: p.Postfix ? null : hm, postfix: p.Postfix ? hm : null);
                lost.Add(new LostPatch($"{p.Target.DeclaringType?.Name}.{p.Target.Name}", p.Patch.Name, owners));
            }
            catch (Exception e)
            {
                MainFile.Logger.Info($"hooks: checking {p.Target.Name} failed: {e.Message}");
            }
        }
        return lost;
    }

    public static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (asm.GetType(fullName) is { } t) return t;
            }
            catch { /* dynamic assemblies can throw */ }
        }
        return null;
    }
}
