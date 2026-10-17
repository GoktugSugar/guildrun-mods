// Writes BepInEx\CustomHeroes_dump.json: every hero, ability, specialization, class, guild and hero pool as the
// game holds them (abilities with the game's own effect/action JSON), plus each hero's animation timings.
// Reference material for writing hero.json files.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BepInEx;
using Ember.Balancing.Common;
using Ember.Balancing.Sheets.Abilities.ActiveAbilities;
using Ember.Balancing.Sheets.Abilities.PassiveAbilities;
using Ember.Balancing.Sheets.Characters;
using Ember.Balancing.Sheets.Characters.Classes;
using Ember.Balancing.Sheets.Characters.Guilds;
using Ember.Balancing.Sheets.Characters.Heroes;
using Ember.Balancing.Sheets.Characters.Specializations;
using Ember.Balancing.Sheets.HeroPools;
using Ember.Balancing.SimulationBridge.AbilityActions;
using Ember.Balancing.SimulationBridge.Effects;
using Ember.Simulation.Core.Fsm.States.ActiveAbilities;
using gg.leyline.unitylocalization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Photon.Deterministic;

namespace CustomHeroes
{
    internal static class Dump
    {
        static double F(FP v) => v.RawValue / 65536.0;
        static string K(LocaKey k) { try { return k?._key; } catch { return null; } }

        public static void Write()
        {
            var path = Path.Combine(Paths.BepInExRootPath, "CustomHeroes_dump.json");
            using var fs = File.Create(path);
            using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
            w.WriteStartObject();

            w.WriteStartArray("heroes");
            foreach (var p in Injector.All("HeroEntry"))
                try { Hero(w, new HeroEntry(p)); } catch (Exception e) { w.WriteStringValue("error: " + e.Message); }
            w.WriteEndArray();

            w.WriteStartArray("activeAbilities");
            foreach (var p in Injector.All("ActiveAbilityEntry"))
                try
                {
                    var a = new ActiveAbilityEntry(p);
                    w.WriteStartObject();
                    w.WriteNumber("id", Injector.Seq(p));
                    w.WriteString("name", K(a._NameLocaKey_k__BackingField));
                    w.WriteString("description", K(a._DescriptionLocaKey_k__BackingField));
                    w.WriteString("targetHero", a._targetHero);
                    w.WriteNumber("rush", F(a._RushDuration_k__BackingField));
                    w.WriteNumber("stall", F(a._StallDuration_k__BackingField));
                    w.WriteString("icon", a._Icon_k__BackingField?.name);
                    w.WriteString("projectile", a._ProjectilePrefab_k__BackingField?.name);
                    w.WriteStartArray("actions");
                    foreach (var x in a._AbilityActions_k__BackingField ?? new Il2CppReferenceArray<IAbilityAction>(0)) Action(w, x);
                    w.WriteEndArray();
                    Effects(w, "effects", a._AbilityEffects_k__BackingField);
                    w.WriteEndObject();
                }
                catch (Exception e) { w.WriteStringValue("error: " + e.Message); }
            w.WriteEndArray();

            w.WriteStartArray("passiveAbilities");
            foreach (var p in Injector.All("PassiveAbilityEntry"))
                try
                {
                    var a = new PassiveAbilityEntry(p);
                    w.WriteStartObject();
                    w.WriteNumber("id", Injector.Seq(p));
                    w.WriteString("name", K(a._NameLocaKey_k__BackingField));
                    w.WriteString("description", K(a._DescriptionLocaKey_k__BackingField));
                    w.WriteString("targetHero", a._targetHero);
                    w.WriteString("icon", a._Icon_k__BackingField?.name);
                    Effects(w, "effects", a._Effects_k__BackingField);
                    w.WriteEndObject();
                }
                catch (Exception e) { w.WriteStringValue("error: " + e.Message); }
            w.WriteEndArray();

            w.WriteStartArray("specializations");
            foreach (var p in Injector.All("HeroSpecializationEntry"))
                try
                {
                    var s = new HeroSpecializationEntry(p);
                    w.WriteStartObject();
                    w.WriteNumber("id", Injector.Seq(p));
                    w.WriteString("name", K(s._NameLocaKey_k__BackingField));
                    w.WriteNumber("class", s._classId);
                    w.WriteString("classDescription", K(s._ClassDescriptionBonusLocaKey_k__BackingField));
                    w.WriteNumber("passive", s._passiveAbilityId);
                    w.WriteNumber("active", s._activeAbilityId);
                    Strings(w, "tags", s._tags);
                    w.WriteEndObject();
                }
                catch (Exception e) { w.WriteStringValue("error: " + e.Message); }
            w.WriteEndArray();

            w.WriteStartArray("specializationChoices");
            foreach (var p in Injector.All("HeroSpecializationChoiceEntry"))
                try
                {
                    var c = new HeroSpecializationChoiceEntry(p);
                    w.WriteStartObject();
                    w.WriteNumber("id", Injector.Seq(p));
                    w.WriteNumber("hero", c._heroId);
                    w.WriteString("specializations", string.Join(",", c._specializationIds ?? new Il2CppStructArray<int>(0)));
                    w.WriteEndObject();
                }
                catch (Exception e) { w.WriteStringValue("error: " + e.Message); }
            w.WriteEndArray();

            w.WriteStartArray("classes");
            foreach (var p in Injector.All("HeroClassEntry"))
                try
                {
                    var c = new HeroClassEntry(p);
                    w.WriteStartObject();
                    w.WriteNumber("id", Injector.Seq(p));
                    w.WriteString("title", c._title);
                    w.WriteString("tag", c._TagName_k__BackingField);
                    w.WriteString("icon", c._IconText_k__BackingField);
                    w.WriteString("name", K(c._NameLocaKey_k__BackingField));
                    w.WriteString("primary", c._PrimaryStat_k__BackingField.ToString());
                    w.WriteString("secondary", string.Join(",", (c._SecondaryStats_k__BackingField ?? new Il2CppStructArray<Ember.Balancing.Sheets.Items.TargetStatType>(0)).Select(x => x.ToString())));
                    w.WriteEndObject();
                }
                catch (Exception e) { w.WriteStringValue("error: " + e.Message); }
            w.WriteEndArray();

            w.WriteStartArray("guilds");
            foreach (var p in Injector.All("GuildEntry"))
                try
                {
                    var g = new GuildEntry(p);
                    w.WriteStartObject();
                    w.WriteNumber("id", Injector.Seq(p));
                    w.WriteString("title", g._title);
                    w.WriteString("tag", g._TagName_k__BackingField);
                    w.WriteString("name", K(g._NameLocaKey_k__BackingField));
                    w.WriteEndObject();
                }
                catch (Exception e) { w.WriteStringValue("error: " + e.Message); }
            w.WriteEndArray();

            w.WriteStartArray("heroPools");
            foreach (var p in Injector.All("HeroPoolEntry"))
                try
                {
                    var pool = new HeroPoolEntry(p);
                    w.WriteStartObject();
                    w.WriteNumber("id", Injector.Seq(p));
                    w.WriteString("title", pool._title);
                    w.WriteString("heroes", string.Join(",", (pool._WeightedRewards_k__BackingField ?? new Il2CppReferenceArray<WeightedHero>(0))
                        .Where(x => x != null).Select(x => $"{x._elementId}:{x._Weight_k__BackingField}")));
                    w.WriteEndObject();
                }
                catch (Exception e) { w.WriteStringValue("error: " + e.Message); }
            w.WriteEndArray();

            w.WriteEndObject();
            w.Flush();
            Plugin.L.LogInfo($"Game data written to {path}");
        }

        static void Hero(Utf8JsonWriter w, HeroEntry h)
        {
            w.WriteStartObject();
            w.WriteNumber("id", Injector.Seq(h.Pointer));
            w.WriteString("name", K(h._NameLocaKey_k__BackingField));
            w.WriteString("alias", K(h._Alias_k__BackingField));
            w.WriteString("subtitle", K(h._Subtitle_k__BackingField));
            w.WriteString("currentGuild", K(h._CurrentGuild_k__BackingField));
            w.WriteString("motivation", K(h._Motivation_k__BackingField));
            w.WriteString("gameplay", K(h._Gameplay_k__BackingField));
            w.WriteBoolean("disabled", h._DisabledInGame_k__BackingField);
            w.WriteNumber("guild", h._guildID);
            w.WriteNumber("class1", h._class1ID);
            w.WriteNumber("class2", h._class2ID);
            Strings(w, "tags", h._tags);
            w.WriteString("price", $"{h._PriceRange_k__BackingField.x}-{h._PriceRange_k__BackingField.y}");
            w.WriteString("gender", h._Gender_k__BackingField.ToString());
            w.WriteNumber("MaxHealth", h._MaxHealth_k__BackingField);
            w.WriteNumber("MaxMana", h._MaxMana_k__BackingField);
            w.WriteNumber("StartingMana", h._StartingMana_k__BackingField);
            w.WriteNumber("ManaRegen", h._ManaRegen_k__BackingField);
            w.WriteNumber("ManaLockTime", F(h._ManaLockTime_k__BackingField));
            w.WriteNumber("MoveSpeed", F(h._MoveSpeed_k__BackingField));
            w.WriteNumber("Defense", h._Defense_k__BackingField);
            w.WriteNumber("BaseAttackDamage", h._BaseAttackDamage_k__BackingField);
            w.WriteNumber("Attack", h._Attack_k__BackingField);
            w.WriteNumber("Magic", h._Magic_k__BackingField);
            w.WriteNumber("AttackRange", h._AttackRange_k__BackingField);
            w.WriteNumber("BaseAttackSpeed", F(h._BaseAttackSpeed_k__BackingField));
            w.WriteNumber("AttackSpeed", h._AttackSpeed_k__BackingField);
            w.WriteNumber("Crit", h._Crit_k__BackingField);
            w.WriteNumber("CircleCollisorRadius", F(h._CircleCollisorRadius_k__BackingField));
            w.WriteString("attackType", h._AttackSettings_k__BackingField._Type_k__BackingField.ToString());
            w.WriteNumber("projectileSpeed", F(h._AttackSettings_k__BackingField._ProjectileSpeed_k__BackingField));
            w.WriteBoolean("startWithActive", h._StartWithActiveAbility_k__BackingField);
            w.WriteNumber("passive", h._passiveAbilityID);
            w.WriteNumber("active", h._activeAbilityID);
            var vc = h.VisualConfig;
            if (vc != null)
            {
                w.WriteString("visualConfig", vc.name);
                w.WriteString("prefab", vc.Prefab?.name);
                var anim = vc.AnimationSettings;
                if (anim != null)
                {
                    w.WriteStartObject("animation");
                    w.WriteBoolean("hasFixedDuration", anim._HasFixedDuration_k__BackingField);
                    w.WriteNumber("fixedDuration", F(anim._FixedDuration_k__BackingField));
                    w.WriteString("actionTimestamps", string.Join(",", (anim._ActionTimestamps_k__BackingField ?? new Il2CppStructArray<FP>(0)).Select(x => F(x).ToString("0.###"))));
                    w.WriteStartArray("timings");
                    foreach (var t in anim._animationTimings ?? new Il2CppReferenceArray<AnimationSettings.AnimationTimings>(0))
                    {
                        if (t == null) continue;
                        w.WriteStartObject();
                        w.WriteString("type", t.Type.ToString());
                        w.WriteNumber("duration", F(t.Duration));
                        w.WriteNumber("hit", F(t.Hit));
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                var au = vc.AudioSettings;
                if (au != null) w.WriteString("audioName", au._OverrideCharacterName_k__BackingField ? au._NameOverride_k__BackingField : "");
            }
            w.WriteEndObject();
        }

        static void Strings(Utf8JsonWriter w, string name, Il2CppStringArray a)
        {
            w.WriteStartArray(name);
            if (a != null) foreach (var s in a) w.WriteStringValue(s);
            w.WriteEndArray();
        }

        static void Action(Utf8JsonWriter w, IAbilityAction x)
        {
            if (x == null) { w.WriteNullValue(); return; }
            string json = null;
            try { json = new BaseAbilityAction(x.Pointer).CreateSerializationInfo()?.SerializedActionJson; } catch (Exception e) { json = "error: " + e.Message; }
            w.WriteStartObject();
            w.WriteString("type", Native.ClassName(x.Pointer));
            w.WriteString("json", json);
            w.WriteEndObject();
        }

        static void Effects(Utf8JsonWriter w, string name, Il2CppReferenceArray<IEffect> list)
        {
            w.WriteStartArray(name);
            if (list != null)
                foreach (var e in list)
                {
                    if (e == null) { w.WriteNullValue(); continue; }
                    string json;
                    try { json = new BaseEffect(e.Pointer).CreateSerializationInfo()?.SerializedEffectJson; } catch (Exception ex) { json = "error: " + ex.Message; }
                    w.WriteStringValue(json ?? Native.ClassName(e.Pointer));
                }
            w.WriteEndArray();
        }
    }
}
