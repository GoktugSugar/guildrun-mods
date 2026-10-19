// Debug: logs when the game runs effects that custom heroes rely on and that are hard to see in a fight
// (every-Nth counters, on-status triggers). Config Debug.TraceEffects; at most MaxLines lines per game start.
using System;
using Ember.Balancing.SimulationBridge.Effects;
using HarmonyLib;

namespace CustomHeroes
{
    internal static class EffectTrace
    {
        const int MaxLines = 600;
        static int _lines;

        public static void Patch(Harmony harmony)
        {
            var m = AccessTools.Method(typeof(BaseEffect), nameof(BaseEffect.Execute));
            if (m == null) { Plugin.L.LogWarning("EffectTrace: BaseEffect.Execute not found"); return; }
            harmony.Patch(m, postfix: new HarmonyMethod(typeof(EffectTrace), nameof(ExecutePostfix)));
            // DoExecute runs only when the effect's conditions passed; ApplyAction once per target the status lands on
            var de = AccessTools.Method(typeof(ModularEffect), nameof(ModularEffect.DoExecute));
            if (de != null) harmony.Patch(de, postfix: new HarmonyMethod(typeof(EffectTrace), nameof(DoExecutePostfix)));
            var ap = AccessTools.Method(typeof(Ember.Balancing.SimulationBridge.Effects.Actions.ApplyStatusWithDurationAction), "ApplyAction");
            if (ap != null) harmony.Patch(ap, postfix: new HarmonyMethod(typeof(EffectTrace), nameof(ApplyStatusPostfix)));
            try
            {
                var probe = new ModularEffect();
                var list = probe._incrementalTriggers;
                var names = new System.Collections.Generic.List<string>();
                if (list != null) foreach (var t in list) names.Add(t.ToString());
                Plugin.L.LogInfo($"EffectTrace: on; triggers that count for Increment: [{string.Join(", ", names)}]");
            }
            catch (Exception e) { Plugin.L.LogInfo("EffectTrace: on (incremental trigger list unreadable: " + e.Message + ")"); }
        }

        static void DoExecutePostfix(ModularEffect __instance) => Log(__instance, "PASSED");

        static void ApplyStatusPostfix(Ember.Balancing.SimulationBridge.Effects.Actions.ApplyStatusWithDurationAction __instance,
            Ember.Balancing.SimulationBridge.Context.IBattleContextReader contextReader, Ember.Balancing.SimulationBridge.EntityId target)
        {
            if (_lines >= MaxLines || __instance == null) return;
            try
            {
                _lines++;
                string who = "?";
                try
                {
                    var c = contextReader?.GetCharacter(target);
                    if (c != null) who = $"{(c.IsHero ? "hero" : "enemy")} rank {c.Rank} hp {c.CurrentHealth}";
                }
                catch (Exception e) { who = "? (" + e.Message + ")"; }
                Plugin.L.LogInfo($"EffectTrace:   applied {__instance.Status} for {__instance.Duration.RawValue / 65536f:0.##}s on {who}");
            }
            catch { }
        }

        static void ExecutePostfix(BaseEffect __instance) => Log(__instance, "run");

        static void Log(BaseEffect __instance, string what)
        {
            if (_lines >= MaxLines || __instance == null) return;
            try
            {
                var trigger = __instance._Trigger_k__BackingField;
                int inc = __instance._TriggerIncrement_k__BackingField;
                string cond = "";
                string acts = "";
                var me = __instance.TryCast<ModularEffect>();
                if (me != null)
                {
                    cond = me._condition == null ? "" : new Il2CppSystem.Object(me._condition.Pointer).GetIl2CppType().Name;
                    if (me._actions != null)
                        foreach (var a in me._actions) if (a != null) acts += (acts.Length > 0 ? "," : "") + new Il2CppSystem.Object(a.Pointer).GetIl2CppType().Name.Replace("Action", "");
                }
                bool watch = inc > 1 || trigger == EffectTriggerType.OnStatusStacksApplied || cond.Contains("Interval") ||
                             acts.Contains("ApplyStatusWithDuration") || trigger == EffectTriggerType.OnStallStart ||
                             acts.Contains("StallDuration") || cond.Contains("PerOwnerRank");
                if (!watch || trigger == EffectTriggerType.OnAutoAttack) return;   // monsters' every-3rd-attack poison floods the log
                _lines++;
                Plugin.L.LogInfo($"EffectTrace: {what} {trigger} x{inc} cond={cond} -> [{acts}]");
            }
            catch (Exception e) { _lines = MaxLines; Plugin.L.LogWarning("EffectTrace off: " + e.Message); }
        }
    }
}
