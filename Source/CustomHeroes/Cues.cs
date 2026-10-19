// Model.Cues: some effects have no animation of their own (Yasuo's Last Breath is a passive strike). A Harmony
// postfix on the cue's action type (ApplyAction, run per target that passed the effect's conditions) spots them by
// trigger and action type and flags the hero; its models play the cue clip on their next frame (CustomHeroModel.LateUpdate).
// The action's target (Last Breath: the stunned enemy) goes along, for cues that blink the model to it.
using System;
using System.Collections.Generic;
using Ember.Balancing.SimulationBridge.Effects;
using HarmonyLib;

namespace CustomHeroes
{
    internal static class Cues
    {
        static readonly List<(string key, CueDef cue, EffectTriggerType trigger)> Watch = new();
        static readonly Dictionary<string, (CueDef cue, long at, string target)> Pending = new();   // hero key -> cue to play, when, on whom (entity guid)
        static readonly object Lock = new();

        public static void Init(Harmony harmony, List<HeroDefinition> heroes)
        {
            foreach (var d in heroes)
                foreach (var c in d.Model?.Cues ?? new List<CueDef>())
                {
                    if (c == null || !Enum.TryParse(c.Trigger, true, out EffectTriggerType t))
                    { Plugin.L.LogWarning($"{d.Name}: cue trigger '{c?.Trigger}' unknown"); continue; }
                    Watch.Add((d.Key, c, t));
                    Plugin.L.LogInfo($"{d.Name}: cue '{c.Clip}' on {t} effects with {c.Action}");
                }
            if (Watch.Count == 0) return;
            // ApplyAction runs once per target that passed the effect's conditions (DoExecute runs for every trigger,
            // conditions or not: Last Breath's started on every status anyone applied)
            var done = new HashSet<string>();
            foreach (var w in Watch)
            {
                if (!done.Add(w.cue.Action)) continue;
                Type type = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.StartsWith("ember")) continue;
                    foreach (var t in asm.GetTypes()) if (t.Name == w.cue.Action && t.Namespace != null && t.Namespace.Contains("Actions")) { type = t; break; }
                    if (type != null) break;
                }
                var m = type == null ? null : AccessTools.Method(type, "ApplyAction");
                if (m == null) { Plugin.L.LogWarning($"Cues: no ApplyAction on '{w.cue.Action}'"); continue; }
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(Cues), nameof(ApplyPostfix)));
            }
        }

        static void ApplyPostfix(Il2CppSystem.Object __instance, EffectTriggerType triggerType, Ember.Balancing.SimulationBridge.EntityId target)
        {
            try
            {
                if (__instance == null) return;
                string name = null;
                foreach (var w in Watch)
                {
                    if (w.trigger != triggerType) continue;
                    name ??= __instance.GetIl2CppType().Name;
                    if (name != w.cue.Action) continue;
                    string who = null;
                    try { who = target.Guid.ToString(); } catch { }
                    lock (Lock) Pending[w.key] = (w.cue, Environment.TickCount64, who);
                    return;
                }
            }
            catch { }
        }

        /// <summary>The cue waiting for this hero, if any (taken: one model plays it), and its target's entity guid.</summary>
        public static CueDef Take(string key, out string target)
        {
            target = null;
            lock (Lock)
            {
                if (Pending.Count == 0 || !Pending.TryGetValue(key, out var c)) return null;
                Pending.Remove(key);
                target = c.target;
                return Environment.TickCount64 - c.at < 500 ? c.cue : null;   // stale (hero was dead or hidden): drop
            }
        }
    }
}
