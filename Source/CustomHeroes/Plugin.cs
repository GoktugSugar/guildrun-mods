using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Ember.Balancing;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace CustomHeroes
{
    [BepInPlugin("gg.mods.guildrun.customheroes", "Guildrun Custom Heroes", "1.0.0")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource L;
        internal static ConfigEntry<bool> Enabled, DumpData;
        internal static ConfigEntry<string> ShaderName, VoiceBus;
        internal static ConfigEntry<float> VoiceVolume;
        internal static List<HeroDefinition> Heroes = new();

        public override void Load()
        {
            L = Log;
            Enabled = Config.Bind("General", "Enabled", true, "Add the heroes defined in Mods\\CustomHeroes\\<Hero>\\hero.json.");
            DumpData = Config.Bind("Debug", "DumpGameData", true, "Write BepInEx\\CustomHeroes_dump.json (heroes, abilities, specializations, classes, guilds, pools) at startup.");
            ShaderName = Config.Bind("Model", "Shader", "Universal Render Pipeline/Unlit", "Shader for the heroes' own models.");
            VoiceVolume = Config.Bind("Voice", "Volume", 1f, "Volume of the heroes' voice lines (on top of the game's voice volume).");
            VoiceBus = Config.Bind("Voice", "Bus", "", "FMOD bus path to play voice lines on (empty = combat lines on bus:/VO/VO_COMB, others on bus:/VO/VO_SHOP).");
            Heroes = HeroDefinition.LoadAll();
            var harmony = new Harmony("gg.mods.guildrun.customheroes");
            harmony.PatchAll(typeof(Patches));
            try { Loca.Patch(harmony); } catch (Exception e) { L.LogError("Loca patch: " + e); }
            try { HeroModel.Patch(harmony); } catch (Exception e) { L.LogError("Model patch: " + e); }
            try { Voice.Init(harmony, Heroes); } catch (Exception e) { L.LogError("Voice: " + e); }
            ClassInjector.RegisterTypeInIl2Cpp<CustomHeroModel>();
            ClassInjector.RegisterTypeInIl2Cpp<Ticker>();
            AddComponent<Ticker>();
            L.LogInfo($"Custom Heroes loaded: {Heroes.Count} hero(es).");
        }
    }

    [HarmonyPatch]
    internal static class Patches
    {
        // The balancing database is complete once the effect providers have filled in every ability's effects.
        [HarmonyPostfix, HarmonyPatch(typeof(EmberBalancing), nameof(EmberBalancing.ApplyEffectsProviders))]
        static void AfterEffects()
        {
            try { Injector.Run("after ApplyEffectsProviders"); }
            catch (Exception e) { Plugin.L.LogError(e); }
        }
    }

    public class Ticker : MonoBehaviour
    {
        public Ticker(IntPtr ptr) : base(ptr) { }
        float _since = -1;

        void Update()
        {
            try
            {
                if (!Injector.Done)
                {   // fallback in case the providers hook never fires: inject 3 s after the database exists
                    bool ready = false;
                    try { ready = EmberBalancing.Instance != null; } catch { }
                    if (ready)
                    {
                        if (_since < 0) _since = Time.unscaledTime;
                        else if (Time.unscaledTime - _since > 3f) Injector.Run("fallback");
                    }
                }
                Loca.Tick();
            }
            catch (Exception e) { Plugin.L.LogError(e); enabled = false; }
        }
    }
}
