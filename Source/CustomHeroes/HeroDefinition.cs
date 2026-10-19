#pragma warning disable CS0649 // fields are filled by the JSON deserializer
// A new hero, loaded from Mods\CustomHeroes\<Hero>\hero.json (comments and trailing commas allowed).
// The hero starts as a copy of an existing one (Template) and everything listed here replaces the copy's data.
// See Mods\CustomHeroes\README.md for the format.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;

namespace CustomHeroes
{
    internal sealed class HeroDefinition
    {
        public string Folder;                        // set by the loader

        public int Id;                               // new hero id (the game uses 1-27)
        public string Key;                           // internal name: visual config, sprites and audio events use it
        public string Name;                          // display name
        public int Template;                         // hero id to copy (stats, prefab/animations, ability shape)
        public string Guild;                         // guild name, tag or title ("Aegis Global", "Corporate")
        public List<string> Classes = new();         // 1-2 of Warrior, Tank, Vanguard, Assassin, Duelist, Mystic, Mage
        public string Gender;                        // Feminine, Masculine or Neutral
        public List<string> Tags;                    // optional: replaces the template's tags
        public Dictionary<string, double> Stats = new();   // CharacterEntry fields: MaxHealth, Defense, MoveSpeed...
        public string AttackType;                    // Melee or Ranged (default: the template's)
        public double ProjectileSpeed;               // ranged only
        public Dictionary<string, string> Lore = new();    // Title, Quote, CurrentWork, Motivation (+ Name)
        public AbilityDef Active;
        public List<SpecDef> Specializations = new();
        public int PoolWeight;                       // weight in each hero pool the template is in (0 = same as template)
        public List<string> Pools;                   // optional: hero pool titles to join instead of the template's
                                                     // ("AllHeroes", "Duelists", "Duelists and Warriors"...)
        public bool StartingBundles = true;          // also offer the hero in the starting bundles
        public ModelDef Model;                       // the hero's own 3D model (replaces the template's in the prefab)
        public PortraitDef Portraits;                // portraits composed from a cut-out render
        public string Voice;                         // folder with one subfolder of clips per voice slot (SKILL_VO, ...)

        public string Path(string rel) => string.IsNullOrEmpty(rel) ? null : System.IO.Path.IsPathRooted(rel) ? rel : System.IO.Path.Combine(Folder, rel);

        static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            IncludeFields = true,
        };

        public static List<HeroDefinition> LoadAll()
        {
            var list = new List<HeroDefinition>();
            var root = System.IO.Path.Combine(Paths.GameRootPath, "Mods", "CustomHeroes");
            if (!Directory.Exists(root)) return list;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var file = System.IO.Path.Combine(dir, "hero.json");
                if (!File.Exists(file)) continue;
                try
                {
                    var d = JsonSerializer.Deserialize<HeroDefinition>(File.ReadAllText(file), Options);
                    d.Folder = dir;
                    if (d.Id <= 0 || string.IsNullOrWhiteSpace(d.Key) || d.Template <= 0)
                        throw new Exception("Id, Key and Template are required");
                    d.Name ??= d.Key;
                    list.Add(d);
                    Plugin.L.LogInfo($"Custom hero {d.Id} '{d.Name}' (copy of hero {d.Template}) from {System.IO.Path.GetFileName(dir)}");
                }
                catch (Exception e) { Plugin.L.LogError($"{file}: {e.Message}"); }
            }
            return list;
        }
    }

    internal sealed class ModelDef
    {
        public string File;                // .glb, relative to the hero folder: skinned mesh, textures, animation clips
        public double Scale = 1;           // size multiplier on top of the hip-height match
        public double HipHeight;           // template's hip height to match (0 = measured in the template prefab)
        public double Yaw;                 // extra rotation in degrees if the model faces the wrong way
        public Dictionary<string, string> Clips = new();   // game animation -> clip name (default Idle/Walk/Attack/Skill/Death);
                                                           // "A,B,C" = take turns (attack combos, Q1/Q2/Q3), restarting each fight
        public Dictionary<string, double> Hits = new();    // clip time of the hit, by clip name or by BasicAttack / SkillAttack
        public bool HideTemplateEffects;   // also switch off the template's particle/trail effects in its prefab (slashes...)
        public bool Warp = true;          // true: attack/skill clips are stretched to the game's timing; false: they play at
                                           // their own speed, started so their hit lands on the game's hit
        public Dictionary<string, double[]> Timings = new();   // game timing override: state -> [duration, hit] seconds
        public List<VfxDef> Vfx = new();   // effects spawned at a clip time
        public List<CueDef> Cues = new();  // clips played when the game runs one of the hero's effects (no animation of its own)
    }

    internal sealed class CueDef
    {
        public string Trigger;             // effect trigger to watch (OnStatusStacksApplied...)
        public string Action;              // and one of its action types (ModifyStatAction...)
        public string Clip;                // clip to play (at its own speed)
        public double Cooldown = 1.5;      // seconds: several hits at once play it once
        public double Burst;               // > 0: a short wind burst of this size (hero heights) on the hero
        public bool Blink;                 // the model jumps to the action's target for the clip, then back (looks only)
    }

    internal sealed class VfxDef
    {
        public string Clip;                // clip that spawns it
        public double At;                  // clip time
        public string Type = "Tornado";
        public double Distance = 4;        // travel, in hero heights
        public double Duration = 0.9;      // seconds
        public double Size = 1.3;          // height, in hero heights
    }

    internal sealed class PortraitDef
    {
        public string Source;              // transparent full-body cut-out, relative to the hero folder
        public string Folder;              // optional: <sprite name>.png here replaces that sprite as is
        public double Scale = 0.8;         // figure size relative to the template hero's
        public double Drop;                // full-body art moves down by this share of the template's height
    }

    internal sealed class AbilityDef
    {
        public int Id;                     // new ability id
        public int Template;               // ability to copy (its actions = the cast's shape and timing, icon, VFX)
        public string Name, Description, DataTracking;
        public string Icon;                // optional PNG, relative to the hero folder
        public double? RushDuration, StallDuration;
        public JsonElement Effects;        // effect list (compact or the game's own JSON); missing = keep the template's
        public List<AttackDef> Attacks;    // optional: replaces the hits of the template's attack action
        public JsonElement ActionFields;   // optional: field values set on the template's actions ({"_stunDuration": 2})
        public JsonElement PreActions;     // optional: ability actions run before the template's (compact JSON, with "Type")
        public JsonElement ExtraActions;   // optional: ability actions run after the template's
    }

    internal sealed class AttackDef
    {
        public double? Delay;              // seconds after the cast starts
        public int? Damage;                // base damage
        public int? ExtraDamagePerRank;
        public List<ScalingDef> Scalings;  // replaces the stat scalings
    }

    internal sealed class ScalingDef
    {
        public string Stat;                // TargetStatType name: Attack, Magic, Defense, MaxHealth...
        public double[] Factors;           // per rank C, B, A, S (one value = all ranks)
    }

    internal sealed class SpecDef
    {
        public int Id;                     // specialization id; its passive gets id "10" + Id (game convention)
        public int Template;               // specialization to copy (default: the template hero's n-th)
        public int PassiveTemplate;        // passive to copy (its icon); default: the template specialization's
        public int PassiveId;              // default "10" + Id
        public List<string> Tags;          // optional: replaces the template's tags (Shields, Rush, Burn...)
        public string Name;                // shown as the specialization's name ("The Glamrock")
        public string Class;               // class the specialization adds (optional)
        public string PassiveName, Description, ClassDescription, DataTracking;
        public string Icon;
        public bool UnlocksActive;         // choosing it unlocks the hero's active ability (heroes that don't start with it)
        public JsonElement Effects;        // passive effects; missing = keep the template's
    }
}
