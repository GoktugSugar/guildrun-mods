// Adds the heroes from hero.json to the game's balancing database once it is built (after the effect providers
// ran, so copied abilities already carry their effects). A new hero is a copy (MemberwiseClone) of its template's
// entries with new ids; the hero, its active ability, the specialization passives, the specializations and the
// specialization choice are registered with LBalancing, and the hero joins every hero pool its template is in.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ember.Balancing;
using Ember.Balancing.Common;
using Ember.Balancing.Sheets.Abilities.ActiveAbilities;
using Ember.Balancing.Sheets.Abilities.PassiveAbilities;
using Ember.Balancing.Sheets.Characters;
using Ember.Balancing.Sheets.Characters.Attacks;
using Ember.Balancing.Sheets.Characters.Classes;
using Ember.Balancing.Sheets.Characters.Guilds;
using Ember.Balancing.Sheets.Characters.Heroes;
using Ember.Balancing.Sheets.Characters.Specializations;
using Ember.Balancing.Sheets.HeroPools;
using Ember.Balancing.Sheets.Items;
using Ember.Balancing.SimulationBridge.AbilityActions;
using Ember.Balancing.SimulationBridge.Common;
using Ember.Balancing.SimulationBridge.Effects;
using Ember.Simulation.Core.Fsm.States.ActiveAbilities;
using gg.leyline.balancing;
using gg.leyline.balancing.Data;
using gg.leyline.unitylocalization;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Photon.Deterministic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomHeroes
{
    internal static class Injector
    {
        public static bool Done;
        static LBalancing _lb;
        static readonly Dictionary<(string cls, int id), IntPtr> Entries = new();
        static int _idOff = -1, _seqOff = -1, _modeOff = -1, _guidOff = -1;
        static Transform _prefabRoot;
        static readonly Il2CppSystem.Collections.Generic.List<Object> Keep = new();

        public static T Pin<T>(T o) where T : Object { if (o != null) Keep.Add(o); return o; }
        public static IEnumerable<IntPtr> All(string cls) => Entries.Where(kv => kv.Key.cls == cls).OrderBy(kv => kv.Key.id).Select(kv => kv.Value);
        public static IntPtr Get(string cls, int id) => Entries.TryGetValue((cls, id), out var p) ? p : IntPtr.Zero;
        public static int Seq(IntPtr entry) => Native.ReadInt(entry + IdOffset(entry) + _seqOff);

        public static void Run(string from)
        {
            if (Done) return;
            var inst = EmberBalancing.Instance;
            if (inst == null) return;
            Done = true;
            _lb = inst.TryCast<LBalancing>() ?? new LBalancing(inst.Pointer);
            Index();
            Plugin.L.LogInfo("Hero ids: " + string.Join(", ", All("HeroEntry").Select(Seq)));
            Plugin.L.LogInfo($"Balancing ready ({from}): " + string.Join(", ", Entries.GroupBy(kv => kv.Key.cls)
                .Where(g => g.Key.Contains("Hero") || g.Key.Contains("Ability") || g.Key.Contains("Guild")).Select(g => $"{g.Count()} {g.Key}")));
            EffectJson.ClassOrGuildId = n => ClassId(n, false) is int c && c > 0 ? c : GuildId(n);
            if (Plugin.DumpData.Value)
                try { Dump.Write(); } catch (Exception e) { Plugin.L.LogError("Dump: " + e); }
            if (!Plugin.Enabled.Value) return;
            foreach (var d in Plugin.Heroes)
                try { AddHero(d); }
                catch (Exception e) { Plugin.L.LogError($"Could not add {d.Name}: {e}"); }
        }

        static void Index()
        {
            var self = _lb.Pointer;
            var dict = Native.ReadPtr(self + Native.ObjFieldOffset(self, "_entryPerId"));
            Native.ScanDictionaryValues(dict, p =>
            {
                if (p == IntPtr.Zero) return;
                if (_idOff < 0) Layout(p);
                Entries[(Native.ClassName(p), Seq(p))] = p;
            });
        }

        static void Layout(IntPtr anyEntry)
        {
            // The id struct's class comes from the field itself: Il2CppClassPointerStore<BalancingEntryId> stays
            // empty until something else touches that interop type, and a -1 offset reads a pointer as the id.
            var cls = Native.il2cpp_object_get_class(anyEntry);
            var idClass = Native.FieldClass(cls, "<Id>k__BackingField");
            _seqOff = Native.FieldOffset(idClass, "_sequentialId") - Native.ObjectHeader;
            _modeOff = Native.FieldOffset(idClass, "_mode") - Native.ObjectHeader;
            _guidOff = Native.FieldOffset(idClass, "<Guid>k__BackingField") - Native.ObjectHeader;
            _idOff = 0;
            if (_seqOff < 0) throw new Exception($"BalancingEntryId layout not found (sequential at {_seqOff})");
            Plugin.L.LogInfo($"BalancingEntryId: sequential +{_seqOff:X}, mode +{_modeOff:X}, guid +{_guidOff:X}");
        }

        // Sheet rows (BalancingSheetEntry) and ScriptableObject entries (BalancingEntry) keep the id at different
        // offsets, so it is looked up per class.
        static readonly Dictionary<IntPtr, int> IdOffsets = new();
        static int IdOffset(IntPtr entry)
        {
            var cls = Native.il2cpp_object_get_class(entry);
            if (!IdOffsets.TryGetValue(cls, out int off)) IdOffsets[cls] = off = Native.FieldOffset(cls, "<Id>k__BackingField");
            if (off < 0) throw new Exception($"{Native.ClassName(entry)} has no Id field");
            return off;
        }

        /// <summary>Gives a cloned entry its own id (same prefix, new sequential number; new Guid for Guid-mode ids).</summary>
        static unsafe void SetId(Il2CppSystem.Object entry, int seq)
        {
            var p = entry.Pointer;
            int id = IdOffset(p);
            Native.WriteInt(p + id + _seqOff, seq);
            if (_modeOff >= 0 && Native.ReadInt(p + id + _modeOff) == 0 && _guidOff >= 0)
            {
                var g = Guid.NewGuid().ToByteArray();
                for (int i = 0; i < 16; i++) *(byte*)(p + id + _guidOff + i) = g[i];
            }
        }

        static void Register(Il2CppSystem.Object entry)
        {
            _lb.RegisterEntry(new IBalancingEntry(entry.Pointer));
            Entries[(Native.ClassName(entry.Pointer), Seq(entry.Pointer))] = entry.Pointer;
        }

        static T Clone<T>(IntPtr template) where T : Il2CppSystem.Object
            => new Il2CppSystem.Object(template).MemberwiseClone().Cast<T>();

        // ------------------------------------------------------------------ names

        static readonly string[] ClassNames = { null, "Warrior", "Tank", "Vanguard", "Assassin", "Duelist", "Mystic", "Mage" };

        public static int ClassId(string name, bool required = true)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            if (int.TryParse(name, out int n)) return n;
            foreach (var p in All("HeroClassEntry"))
            {
                var c = new HeroClassEntry(p);
                if (Is(name, c._title, c._TagName_k__BackingField, KeyOf(c._NameLocaKey_k__BackingField))) return Seq(p);
            }
            int i = Array.FindIndex(ClassNames, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            if (i > 0) return i;
            if (required) throw new Exception($"unknown class '{name}'");
            return 0;
        }

        public static int GuildId(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            if (int.TryParse(name, out int n)) return n;
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {   // display names -> the guild entries' titles
                ["The Hunt"] = "Street", ["Hunt"] = "Street", ["Urban"] = "Street", ["Frontline"] = "Sporty", ["Athletic"] = "Sporty",
                ["Gaia Project"] = "Bohemian", ["Gaia"] = "Bohemian", ["A Temple"] = "Punk", ["Temple"] = "Punk",
                ["L'Héritage"] = "Luxury", ["L'Heritage"] = "Luxury", ["Heritage"] = "Luxury", ["Aegis Global"] = "Corporate", ["Aegis"] = "Corporate",
            };
            var want = aliases.TryGetValue(name, out var a) ? a : name;
            foreach (var p in All("GuildEntry"))
            {
                var g = new GuildEntry(p);
                if (Is(want, g._title, g._TagName_k__BackingField, KeyOf(g._NameLocaKey_k__BackingField))) return Seq(p);
            }
            throw new Exception($"unknown guild '{name}'");
        }

        static bool Is(string want, params string[] names)
        {
            foreach (var n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                if (string.Equals(n, want, StringComparison.OrdinalIgnoreCase)) return true;
                var last = n.Split('.', '_', ' ').LastOrDefault();
                if (string.Equals(last, want, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static string KeyOf(LocaKey k) { try { return k?._key; } catch { return null; } }

        /// <summary>New LocaKey modelled on the template's (same table, key with the new id/name), or the template's key
        /// when there is no new text.</summary>
        static LocaKey NewKey(LocaKey template, string text, Func<string, string> remap, string fallbackTable, string fallbackKey)
        {
            if (text == null) return template;
            string key = KeyOf(template), table = null;
            try { table = template?._cachedTable; } catch { }
            key = !string.IsNullOrEmpty(key) ? remap(key) : fallbackKey;
            if (string.IsNullOrEmpty(key)) return template;
            if (string.IsNullOrEmpty(table)) table = key.Contains('.') ? key[..key.IndexOf('.')] : fallbackTable;
            return Loca.Key(table, key, text);
        }

        /// <summary>"No text": the template's key when that one is empty, else an empty key borrowed from the game
        /// (a new LocaKey("") shows "No translation found"). The game's keys refer to their entries by id, so an
        /// empty key name doesn't mean an empty key; the LocalizedString tells.</summary>
        static LocaKey NoKey(LocaKey template) => IsEmptyKey(template) ? template : EmptyKey();

        static bool IsEmptyKey(LocaKey k)
        {
            if (k == null) return false;
            if (!string.IsNullOrEmpty(KeyOf(k))) return false;
            try { var ls = k._localizedString; return ls == null || ls.IsEmpty; } catch { return false; }
        }

        static LocaKey _emptyKey;
        static LocaKey EmptyKey()
        {
            if (_emptyKey != null) return _emptyKey;
            foreach (var p in All("PassiveAbilityEntry"))
            {
                var k = new PassiveAbilityEntry(p)._DataTrackingDescriptionLocaKey_k__BackingField;
                if (IsEmptyKey(k)) return _emptyKey = k;
            }
            Plugin.L.LogWarning("No empty LocaKey found in the game data; using a new one");
            return _emptyKey = new LocaKey("", null);
        }

        static Func<string, string> IdRemap(int oldId, int newId) =>
            k => Regex.Replace(k, $@"_{oldId}(?=\.|$)", "_" + newId);

        // ------------------------------------------------------------------ the hero

        static void AddHero(HeroDefinition d)
        {
            if (Get("HeroEntry", d.Id) != IntPtr.Zero) throw new Exception($"hero id {d.Id} is already used");
            var tp = Get("HeroEntry", d.Template);
            if (tp == IntPtr.Zero) throw new Exception($"template hero {d.Template} not found");
            var t = new HeroEntry(tp);
            var h = Clone<HeroEntry>(tp);
            SetId(h, d.Id);
            h._DisabledInGame_k__BackingField = false;
            string tKey = TemplateName(t);

            // names and lore (the game looks hero names up as Heroes.Hero_<id>.Name; the entry's own key is empty)
            h._NameLocaKey_k__BackingField = NewKey(t._NameLocaKey_k__BackingField, d.Name, IdRemap(d.Template, d.Id), "Heroes", $"Heroes.Hero_{d.Id}.Name");
            Loca.Key("Heroes", $"Heroes.Hero_{d.Id}.Name", d.Name);
            Loca.Key("Lore", $"Lore.Hero.Name.{d.Key}", d.Name);
            string Lore(string field)
            {
                if (d.Lore.TryGetValue(field, out var v)) return v;
                return field == "Name" ? d.Name : null;
            }
            LocaKey LoreKey(LocaKey tk, string fieldGuess)
            {
                var key = KeyOf(tk);
                if (string.IsNullOrEmpty(key)) return tk;   // unused by the template (Gameplay)
                string seg = fieldGuess;
                if (key != null)
                {
                    var parts = key.Split('.');
                    if (parts.Length == 4 && parts[0] == "Lore" && parts[1] == "Hero") seg = parts[2];
                }
                return NewKey(tk, Lore(seg), k =>
                {
                    var parts = k.Split('.');
                    if (parts.Length == 4 && parts[0] == "Lore") { parts[3] = d.Key; return string.Join(".", parts); }
                    return $"Lore.Hero.{seg}.{d.Key}";
                }, "Lore", $"Lore.Hero.{seg}.{d.Key}");
            }
            h._Alias_k__BackingField = LoreKey(t._Alias_k__BackingField, "Name");
            h._Subtitle_k__BackingField = LoreKey(t._Subtitle_k__BackingField, "Title");
            h._CurrentGuild_k__BackingField = LoreKey(t._CurrentGuild_k__BackingField, "CurrentWork");
            h._Motivation_k__BackingField = LoreKey(t._Motivation_k__BackingField, "Motivation");
            h._Gameplay_k__BackingField = LoreKey(t._Gameplay_k__BackingField, "Quote");

            // guild, classes, look
            if (d.Guild != null) h._guildID = GuildId(d.Guild);
            if (d.Classes.Count > 0)
            {
                h._class1ID = ClassId(d.Classes[0]);
                h._class2ID = d.Classes.Count > 1 ? ClassId(d.Classes[1]) : -1;   // the game's "none" is -1
            }
            if (d.Gender != null) h._Gender_k__BackingField = Enum.Parse<Gender>(d.Gender, true);
            if (d.Tags != null) h._tags = new Il2CppStringArray(d.Tags.ToArray());
            if (d.AttackType != null)
            {
                var s = h._AttackSettings_k__BackingField;
                s._Type_k__BackingField = Enum.Parse<AttackType>(d.AttackType, true);
                if (d.ProjectileSpeed > 0) s._ProjectileSpeed_k__BackingField = Fp(d.ProjectileSpeed);
                h._AttackSettings_k__BackingField = s;
            }
            foreach (var (stat, v) in d.Stats) SetStat(h, stat, v);

            // abilities
            if (d.Active != null) h._activeAbilityID = AddActive(d, t);
            var specs = AddSpecializations(d, t, out var passiveMap);
            // Two layouts in the game: heroes that start with their active ability get every passive from a
            // specialization (Kai); the others start with a passive and one specialization unlocks the active (Skorn).
            if (h._StartWithActiveAbility_k__BackingField) h._passiveAbilityID = -1;
            else if (passiveMap.TryGetValue(t._passiveAbilityID, out int startPassive)) h._passiveAbilityID = startPassive;
            else if (t._passiveAbilityID > 0) Plugin.L.LogWarning($"{d.Name}: template passive {t._passiveAbilityID} is no specialization passive; kept");
            if (!h._StartWithActiveAbility_k__BackingField && specs != null && !d.Specializations.Exists(x => x.UnlocksActive))
                Plugin.L.LogWarning($"{d.Name}: doesn't start with the active ability and no specialization unlocks it (set StartWithActiveAbility or UnlocksActive)");

            // visuals
            var vc = CloneVisualConfig(t.VisualConfig, d, tKey);
            if (vc != null) h._VisualConfig_k__BackingField = vc;

            Register(h);
            if (specs != null) Register(specs);
            JoinPools(d);
            Plugin.L.LogInfo($"Added hero {d.Id} '{d.Name}' ({string.Join("/", d.Classes)} = {h._class1ID}/{h._class2ID}, guild {h._guildID}) from template {d.Template} '{tKey}', " +
                $"active {h._activeAbilityID}, passive {h._passiveAbilityID}, HP {h._MaxHealth_k__BackingField}, visual config '{vc?.name}'.");
        }

        static string TemplateName(HeroEntry t)
        {
            try
            {
                var vc = t.VisualConfig;
                if (vc != null) return vc.name.Replace("_VisualConfig", "").Replace("VisualConfig", "");
            }
            catch { }
            return KeyOf(t._Alias_k__BackingField)?.Split('.').LastOrDefault() ?? "Template";
        }

        static FP Fp(double v) => FP.FromRaw((long)Math.Round(v * 65536.0));

        static void SetStat(HeroEntry h, string stat, double v)
        {
            int i = (int)Math.Round(v);
            switch (stat.ToLowerInvariant())
            {
                case "maxhealth": h._MaxHealth_k__BackingField = i; break;
                case "maxmana": h._MaxMana_k__BackingField = i; break;
                case "startingmana": h._StartingMana_k__BackingField = i; break;
                case "manaregen": h._ManaRegen_k__BackingField = i; break;
                case "movespeed": h._MoveSpeed_k__BackingField = Fp(v); break;
                case "defense": h._Defense_k__BackingField = i; break;
                case "baseattackdamage": h._BaseAttackDamage_k__BackingField = i; break;
                case "attack": h._Attack_k__BackingField = i; break;
                case "magic": h._Magic_k__BackingField = i; break;
                case "attackrange": h._AttackRange_k__BackingField = i; break;
                case "baseattackspeed": h._BaseAttackSpeed_k__BackingField = Fp(v); break;
                case "attackspeed": h._AttackSpeed_k__BackingField = i; break;
                case "crit": h._Crit_k__BackingField = i; break;
                case "manalocktime": h._ManaLockTime_k__BackingField = Fp(v); break;
                case "circlecollisorradius": h._CircleCollisorRadius_k__BackingField = Fp(v); break;
                case "startwithactiveability": h._StartWithActiveAbility_k__BackingField = v != 0; break;
                case "pricemin": { var r = h._PriceRange_k__BackingField; r.x = i; h._PriceRange_k__BackingField = r; break; }
                case "pricemax": { var r = h._PriceRange_k__BackingField; r.y = i; h._PriceRange_k__BackingField = r; break; }
                default: Plugin.L.LogWarning($"Unknown stat '{stat}'"); break;
            }
        }

        // ------------------------------------------------------------------ abilities

        static int AddActive(HeroDefinition d, HeroEntry t)
        {
            var a = d.Active;
            int templateId = a.Template > 0 ? a.Template : t._activeAbilityID;
            var tp = Get("ActiveAbilityEntry", templateId);
            if (tp == IntPtr.Zero) throw new Exception($"template active ability {templateId} not found");
            if (Get("ActiveAbilityEntry", a.Id) != IntPtr.Zero) throw new Exception($"active ability id {a.Id} is already used");
            var ta = new ActiveAbilityEntry(tp);
            var e = Clone<ActiveAbilityEntry>(tp);
            SetId(e, a.Id);
            var remap = IdRemap(templateId, a.Id);
            e._NameLocaKey_k__BackingField = NewKey(ta._NameLocaKey_k__BackingField, a.Name, remap, "ActiveAbilities", $"ActiveAbilities.ActiveAbility_{a.Id}.Name");
            e._DescriptionLocaKey_k__BackingField = NewKey(ta._DescriptionLocaKey_k__BackingField, a.Description, remap, "ActiveAbilities", $"ActiveAbilities.ActiveAbility_{a.Id}.Description");
            e._DataTrackingDescriptionLocaKey_k__BackingField = a.DataTracking == null ? NoKey(ta._DataTrackingDescriptionLocaKey_k__BackingField)
                : NewKey(ta._DataTrackingDescriptionLocaKey_k__BackingField, a.DataTracking, remap, "ActiveAbilities", $"ActiveAbilities.ActiveAbility_{a.Id}.DataTrackingDescription");
            e._targetHero = d.Name;
            if (a.RushDuration.HasValue) e._RushDuration_k__BackingField = Fp(a.RushDuration.Value);
            if (a.StallDuration.HasValue) e._StallDuration_k__BackingField = Fp(a.StallDuration.Value);
            var icon = LoadSprite(d.Path(a.Icon), $"{d.Key}_Active");
            if (icon != null) e._Icon_k__BackingField = icon;
            else if (e._Icon_k__BackingField == null && t._activeAbilityID > 0 && Get("ActiveAbilityEntry", t._activeAbilityID) is var hp && hp != IntPtr.Zero)
                e._Icon_k__BackingField = new ActiveAbilityEntry(hp)._Icon_k__BackingField;   // monster abilities have no icon
            if (a.Effects.ValueKind == JsonValueKind.Array)
                try { e._AbilityEffects_k__BackingField = Effects(a.Effects, $"{d.Name} active"); }
                catch (Exception ex) { Plugin.L.LogError($"{d.Name}: active effects failed, keeping the template's: {ex.Message}"); }
            if (a.Attacks?.Count > 0 || a.ActionFields.ValueKind == JsonValueKind.Object
                || a.PreActions.ValueKind == JsonValueKind.Array || a.ExtraActions.ValueKind == JsonValueKind.Array)
                try { e._AbilityActions_k__BackingField = Actions(ta._AbilityActions_k__BackingField, a, d.Name); }
                catch (Exception ex) { Plugin.L.LogError($"{d.Name}: ability actions failed, keeping the template's: {ex}"); }
            Register(e);
            Plugin.L.LogInfo($"{d.Name}: active ability {a.Id} '{a.Name}' (copy of {templateId}), {e._AbilityEffects_k__BackingField?.Length ?? 0} effect(s), " +
                $"actions [{string.Join(", ", (e._AbilityActions_k__BackingField ?? new Il2CppReferenceArray<IAbilityAction>(0)).Select(x => Native.ClassName(x.Pointer)))}]");
            return a.Id;
        }

        /// <summary>The ability's actions, run one after another: PreActions, deep copies of the template's (hits
        /// replaced by Attacks, fields set by ActionFields), ExtraActions.</summary>
        static Il2CppReferenceArray<IAbilityAction> Actions(Il2CppReferenceArray<IAbilityAction> src, AbilityDef a, string who)
        {
            var copies = new List<IAbilityAction>();
            if (src != null) for (int i = 0; i < src.Length; i++) copies.Add(src[i].DeepClone());
            if (a.Attacks?.Count > 0) SetAttacks(copies, a.Attacks, who);
            if (a.ActionFields.ValueKind == JsonValueKind.Object)
                foreach (var p in a.ActionFields.EnumerateObject())
                {
                    var hit = new List<string>();
                    foreach (var c in copies)
                        if (EffectJson.SetField(c.Pointer, p.Name, p.Value)) hit.Add(Native.ClassName(c.Pointer));
                    if (hit.Count == 0) Plugin.L.LogWarning($"{who}: no ability action has a field '{p.Name}'");
                    else Plugin.L.LogInfo($"{who}: {p.Name} = {p.Value.GetRawText()} on {string.Join(", ", hit)}");
                }
            var all = NewActions(a.PreActions, who);
            all.AddRange(copies);
            all.AddRange(NewActions(a.ExtraActions, who));
            var res = new Il2CppReferenceArray<IAbilityAction>(all.Count);
            for (int i = 0; i < all.Count; i++) res[i] = all[i];
            return res;
        }

        /// <summary>Ability actions from hero.json, deserialized the way the game loads its own.</summary>
        static List<IAbilityAction> NewActions(JsonElement list, string who)
        {
            var res = new List<IAbilityAction>();
            if (list.ValueKind != JsonValueKind.Array) return res;
            foreach (var x in list.EnumerateArray())
            {
                string json = null;
                IAbilityAction act;
                try
                {
                    json = EffectJson.Object(x);
                    string typeName;
                    using (var doc = JsonDocument.Parse(json)) typeName = doc.RootElement.GetProperty("$type").GetString();
                    var type = Il2CppType.TypeFromPointer(EffectJson.ClassOfTypeName(typeName));
                    act = new ActionSerializationInfo(json, type).Deserialize();
                    if (act == null) throw new Exception("did not deserialize");
                }
                catch (Exception e)
                {
                    Plugin.L.LogError($"{who}: ability action skipped ({e.Message}):\n  {json ?? x.GetRawText()}");
                    continue;
                }
                string detail = "";
                var ea = act.TryCast<EffectAbilityAction>();
                if (ea != null && ea._effectBlocks != null)
                {
                    var blocks = new List<string>();
                    foreach (var b in ea._effectBlocks)
                        blocks.Add($"{b._Delay_k__BackingField.RawValue / 65536.0:0.##}s: {b._Effects_k__BackingField?.Length ?? 0} effect(s) for {b._Duration_k__BackingField.RawValue / 65536.0:0.##}s");
                    detail = " [" + string.Join("; ", blocks) + "]";
                }
                Plugin.L.LogInfo($"{who}: new ability action {Native.ClassName(act.Pointer)}{detail}:\n  {json}");
                res.Add(act);
            }
            return res;
        }

        /// <summary>Replaces the hits of the first multi-attack action.</summary>
        static void SetAttacks(List<IAbilityAction> actions, List<AttackDef> attacks, string who)
        {
            bool done = false;
            foreach (var copy in actions)
            {
                if (done) break;
                var multi = copy.TryCast<MultiAttackAbilityAction>();
                if (multi == null) continue;
                var old = multi._attacks?._attacks;
                var list = new Il2CppReferenceArray<AttackData>(attacks.Count);
                for (int k = 0; k < attacks.Count; k++)
                {
                    var o = old != null && old.Length > 0 ? old[Math.Min(k, old.Length - 1)] : null;
                    var ad = new AttackData();
                    ad._Delay_k__BackingField = attacks[k].Delay.HasValue ? Fp(attacks[k].Delay.Value) : o?._Delay_k__BackingField ?? Fp(0.3);
                    ad._Damage_k__BackingField = attacks[k].Damage ?? o?._Damage_k__BackingField ?? 0;
                    ad._ExtraDamagePerRank_k__BackingField = attacks[k].ExtraDamagePerRank ?? o?._ExtraDamagePerRank_k__BackingField ?? 0;
                    if (attacks[k].Scalings != null)
                    {
                        var sc = new Il2CppReferenceArray<DamageScaling>(attacks[k].Scalings.Count);
                        for (int s = 0; s < sc.Length; s++)
                        {
                            var def = attacks[k].Scalings[s];
                            double F(int r) => def.Factors.Length == 0 ? 0 : def.Factors[Math.Min(r, def.Factors.Length - 1)];
                            sc[s] = new DamageScaling
                            {
                                Stat = Enum.Parse<TargetStatType>(def.Stat, true),
                                Rank1Factor = Fp(F(0)), Rank2Factor = Fp(F(1)), Rank3Factor = Fp(F(2)), Rank4Factor = Fp(F(3)),
                            };
                        }
                        ad._Scalings_k__BackingField = sc;
                    }
                    else ad._Scalings_k__BackingField = o?._Scalings_k__BackingField;
                    list[k] = ad;
                }
                var holder = new AbilityActionAttacks();
                holder._attacks = list;
                multi._attacks = holder;
                done = true;
                Plugin.L.LogInfo($"{who}: {attacks.Count} hit(s) on {Native.ClassName(copy.Pointer)}");
            }
            if (!done) Plugin.L.LogWarning($"{who}: the template ability has no attack action; Attacks ignored.");
        }

        static Il2CppReferenceArray<IEffect> Effects(JsonElement list, string who)
        {
            var jsons = new List<string>();
            foreach (var e in list.EnumerateArray()) jsons.AddRange(EffectJson.Effects(e));
            Plugin.L.LogInfo($"{who}: effects JSON:\n  " + string.Join("\n  ", jsons));
            var res = new Il2CppReferenceArray<IEffect>(jsons.Count);
            for (int i = 0; i < jsons.Count; i++)
            {
                IEffect fx = null;
                try { fx = new EffectSerializationInfo(jsons[i], Il2CppType.TypeFromPointer(EffectJson.EffectClass(jsons[i]))).Deserialize(); }
                catch (Exception e) { Plugin.L.LogWarning($"{who}: effect {i} via EffectSerializationInfo: {e.Message}"); }
                if (fx == null)
                {
                    var arr = EffectSerializer.DeserializeEffects("[" + jsons[i] + "]");
                    if (arr != null && arr.Length > 0) fx = arr[0];
                }
                if (fx == null) throw new Exception($"{who}: effect {i} did not deserialize");
                res[i] = fx;
            }
            return res;
        }

        static HeroSpecializationChoiceEntry AddSpecializations(HeroDefinition d, HeroEntry t, out Dictionary<int, int> passiveMap)
        {
            passiveMap = new Dictionary<int, int>();   // template passive id -> new passive id
            if (d.Specializations.Count == 0) return null;
            HeroSpecializationChoiceEntry tc = null;
            foreach (var p in All("HeroSpecializationChoiceEntry"))
            {
                var c = new HeroSpecializationChoiceEntry(p);
                if (c._heroId == d.Template) { tc = c; break; }
            }
            if (tc == null) throw new Exception($"template hero {d.Template} has no specialization choice");
            var tIds = tc._specializationIds;
            var ids = new List<int>();
            for (int i = 0; i < d.Specializations.Count; i++)
            {
                var sd = d.Specializations[i];
                int tid = sd.Template > 0 ? sd.Template : tIds[Math.Min(i, tIds.Length - 1)];
                var tsp = Get("HeroSpecializationEntry", tid);
                if (tsp == IntPtr.Zero) throw new Exception($"template specialization {tid} not found");
                var ts = new HeroSpecializationEntry(tsp);
                var s = Clone<HeroSpecializationEntry>(tsp);
                SetId(s, sd.Id);
                var remap = IdRemap(tid, sd.Id);
                s._NameLocaKey_k__BackingField = NewKey(ts._NameLocaKey_k__BackingField, sd.Name, remap, "HeroSpecializations", $"HeroSpecializations.HeroSpecialization_{sd.Id}.Name");
                s._classId = sd.Class != null ? ClassId(sd.Class) : -1;
                s._activeAbilityId = sd.UnlocksActive && d.Active != null ? d.Active.Id : -1;
                if (sd.Tags != null) s._tags = new Il2CppStringArray(sd.Tags.ToArray());

                int pid = sd.PassiveId > 0 ? sd.PassiveId : int.Parse("10" + sd.Id);
                int tpid = sd.PassiveTemplate > 0 ? sd.PassiveTemplate : ts._passiveAbilityId;
                if (tpid <= 0) tpid = int.Parse("10" + tid);   // some specializations keep their passive on the hero
                var tpp = Get("PassiveAbilityEntry", tpid);
                if (tpp == IntPtr.Zero) throw new Exception($"template passive {tpid} not found");
                var tpa = new PassiveAbilityEntry(tpp);
                var pa = Clone<PassiveAbilityEntry>(tpp);
                SetId(pa, pid);
                var premap = IdRemap(tpid, pid);
                pa._NameLocaKey_k__BackingField = NewKey(tpa._NameLocaKey_k__BackingField, sd.PassiveName ?? sd.Name, premap, "PassiveAbilities", $"PassiveAbilities.PassiveAbility_{pid}.Name");
                pa._DescriptionLocaKey_k__BackingField = NewKey(tpa._DescriptionLocaKey_k__BackingField, sd.Description, premap, "PassiveAbilities", $"PassiveAbilities.PassiveAbility_{pid}.Description");
                pa._DataTrackingDescriptionLocaKey_k__BackingField = sd.DataTracking == null ? NoKey(tpa._DataTrackingDescriptionLocaKey_k__BackingField)
                    : NewKey(tpa._DataTrackingDescriptionLocaKey_k__BackingField, sd.DataTracking, premap, "PassiveAbilities", $"PassiveAbilities.PassiveAbility_{pid}.DataTrackingDescription");
                pa._targetHero = d.Name;
                var icon = LoadSprite(d.Path(sd.Icon), $"{d.Key}_Spec{i + 1}");
                if (icon != null) pa._Icon_k__BackingField = icon;
                if (sd.Effects.ValueKind == JsonValueKind.Array)
                    try { pa._Effects_k__BackingField = Effects(sd.Effects, $"{d.Name} {sd.Name}"); }
                    catch (Exception ex) { Plugin.L.LogError($"{d.Name} {sd.Name}: effects failed, keeping the template's: {ex.Message}"); }
                Register(pa);
                passiveMap[tpid] = pid;

                s._passiveAbilityId = pid;
                // the game's key is PassiveAbilities.PassiveAbility_<passive>.ClassDescription (referenced by id, so the name reads empty)
                var cdKey = KeyOf(ts._ClassDescriptionBonusLocaKey_k__BackingField);
                s._ClassDescriptionBonusLocaKey_k__BackingField = sd.ClassDescription == null ? NoKey(ts._ClassDescriptionBonusLocaKey_k__BackingField)
                    : Loca.Key("PassiveAbilities", !string.IsNullOrEmpty(cdKey) ? IdRemap(tpid, pid)(cdKey) : $"PassiveAbilities.PassiveAbility_{pid}.ClassDescription", sd.ClassDescription);
                if (sd.Class != null && sd.ClassDescription == null) Plugin.L.LogWarning($"{sd.Name}: adds a class but has no ClassDescription");
                Register(s);
                ids.Add(sd.Id);
                Plugin.L.LogInfo($"{d.Name}: specialization {sd.Id} '{sd.Name}' (copy of {tid}), class {s._classId}, passive {pid} (copy of {tpid}), {pa._Effects_k__BackingField?.Length ?? 0} effect(s)");
            }
            var choice = Clone<HeroSpecializationChoiceEntry>(tc.Pointer);
            int seq = Seq(tc.Pointer) == d.Template ? d.Id : Seq(tc.Pointer) + (d.Id - d.Template);
            while (Get("HeroSpecializationChoiceEntry", seq) != IntPtr.Zero) seq++;
            SetId(choice, seq);
            choice._heroId = d.Id;
            choice._specializationIds = new Il2CppStructArray<int>(ids.ToArray());
            return choice;
        }

        // ------------------------------------------------------------------ pools

        static void JoinPools(HeroDefinition d)
        {
            int joined = 0;
            foreach (var p in All("HeroPoolEntry"))
            {
                var pool = new HeroPoolEntry(p);
                var arr = pool._WeightedRewards_k__BackingField;
                if (arr == null) continue;
                int weight = -1;
                bool already = false;
                foreach (var w in arr)
                {
                    if (w == null) continue;
                    if (w._elementId == d.Template) weight = w._Weight_k__BackingField;
                    if (w._elementId == d.Id) already = true;
                }
                if (weight < 0 || already) continue;
                var bigger = new Il2CppReferenceArray<WeightedHero>(arr.Length + 1);
                for (int i = 0; i < arr.Length; i++) bigger[i] = arr[i];
                bigger[arr.Length] = new WeightedHero(d.Id, d.PoolWeight > 0 ? d.PoolWeight : weight);
                pool._WeightedRewards_k__BackingField = bigger;
                joined++;
                Plugin.L.LogInfo($"{d.Name}: joined hero pool {Seq(p)} '{pool._title}' (weight {(d.PoolWeight > 0 ? d.PoolWeight : weight)})");
            }
            if (joined == 0) Plugin.L.LogWarning($"{d.Name}: template hero {d.Template} is in no hero pool; {d.Name} won't show up in shops.");
        }

        // ------------------------------------------------------------------ visuals

        static CharacterVisualConfig CloneVisualConfig(CharacterVisualConfig src, HeroDefinition d, string tKey)
        {
            if (src == null) { Plugin.L.LogWarning($"{d.Name}: template has no visual config"); return null; }
            var vc = Object.Instantiate(src);
            vc.name = d.Key + "_VisualConfig";
            vc.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Keep.Add(vc);

            // own prefabs, with the hero's own model built in
            var prefabs = new Dictionary<IntPtr, GameObject>();
            GameObject P(GameObject g)
            {
                if (g == null) return null;
                if (!prefabs.TryGetValue(g.Pointer, out var c)) prefabs[g.Pointer] = c = ClonePrefab(g, d);
                return c;
            }
            vc._Prefab_k__BackingField = P(src.Prefab);
            var ranks = src.PrefabsPerRank;
            if (ranks != null)
            {
                var arr = new Il2CppReferenceArray<GameObject>(ranks.Length);
                for (int i = 0; i < ranks.Length; i++) arr[i] = P(ranks[i]);
                vc._PrefabsPerRank_k__BackingField = arr;
            }

            // own sprites, named after the hero: composed from its portrait render, or copies of the template's
            var names = new[] { tKey, TemplateDisplayName(d.Template) }.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToArray();
            var all = new List<Sprite>();
            void Gather(Sprite s) { if (s != null && !all.Exists(x => x.Pointer == s.Pointer)) all.Add(s); }
            void GatherA(Il2CppReferenceArray<Sprite> a) { if (a != null) foreach (var s in a) Gather(s); }
            Gather(src.SmallPortraitSprite); Gather(src.SquarePortraitSprite); Gather(src.PortraitSprite);
            GatherA(src.FullPortraitSpritesPerRank); GatherA(src.PortraitSpritesPerRank); GatherA(src.MediumPortraitSpritesPerRank);
            GatherA(src.BreakoutPortraitSpritesPerRank); GatherA(src.HalfBodyPortraitPerRank);
            var sprites = Portraits.Make(d, all.Select(s => (s, SpriteName(s.name, names, d.Key))).ToList());
            Sprite S(Sprite s)
            {
                if (s == null) return null;
                if (!sprites.TryGetValue(s.Pointer, out var c)) sprites[s.Pointer] = c = CloneSprite(s, names, d.Key);
                return c;
            }
            Il2CppReferenceArray<Sprite> SA(Il2CppReferenceArray<Sprite> a)
            {
                if (a == null) return null;
                var r = new Il2CppReferenceArray<Sprite>(a.Length);
                for (int i = 0; i < a.Length; i++) r[i] = S(a[i]);
                return r;
            }
            vc._SmallPortraitSprite_k__BackingField = S(src.SmallPortraitSprite);
            vc._SquarePortraitSprite_k__BackingField = S(src.SquarePortraitSprite);
            vc._PortraitSprite_k__BackingField = S(src.PortraitSprite);
            vc._FullPortraitSpritesPerRank_k__BackingField = SA(src.FullPortraitSpritesPerRank);
            vc._PortraitSpritesPerRank_k__BackingField = SA(src.PortraitSpritesPerRank);
            vc._MediumPortraitSpritesPerRank_k__BackingField = SA(src.MediumPortraitSpritesPerRank);
            vc._BreakoutPortraitSpritesPerRank_k__BackingField = SA(src.BreakoutPortraitSpritesPerRank);
            vc._HalfBodyPortraitPerRank_k__BackingField = SA(src.HalfBodyPortraitPerRank);

            // audio events are built from this name
            var au = new Ember.Balancing.Sheets.Characters.AudioSettings();
            var tau = src.AudioSettings;
            if (tau != null)
            {
                au._OverrideDeathSFX_k__BackingField = tau._OverrideDeathSFX_k__BackingField;
                au._OverrideAttackSFX_k__BackingField = tau._OverrideAttackSFX_k__BackingField;
                au._OverrideAttackHitSFX_k__BackingField = tau._OverrideAttackHitSFX_k__BackingField;
                au._OverrideSkillSFX_k__BackingField = tau._OverrideSkillSFX_k__BackingField;
                au._OverrideSkillHitSFX_k__BackingField = tau._OverrideSkillHitSFX_k__BackingField;
            }
            au._OverrideCharacterName_k__BackingField = true;
            au._NameOverride_k__BackingField = d.Key;
            vc._AudioSettings_k__BackingField = au;

            try { vc.RefreshCachedTimings(); } catch (Exception e) { Plugin.L.LogWarning($"{d.Name}: RefreshCachedTimings: {e.Message}"); }
            HeroModel.ReadTimings(d, vc);
            Plugin.L.LogInfo($"{d.Name}: visual config '{vc.name}' from '{src.name}', {prefabs.Count} prefab(s), {sprites.Count} sprite(s) [{string.Join(", ", sprites.Values.Select(s => s.name))}]");
            return vc;
        }

        static string TemplateDisplayName(int heroId)
        {
            var p = Get("HeroEntry", heroId);
            if (p == IntPtr.Zero) return null;
            return KeyOf(new HeroEntry(p)._Alias_k__BackingField)?.Split('.').LastOrDefault();
        }

        static GameObject ClonePrefab(GameObject src, HeroDefinition d)
        {
            if (_prefabRoot == null)
            {
                var root = new GameObject("CustomHeroes_Prefabs");
                root.SetActive(false);
                Object.DontDestroyOnLoad(root);
                _prefabRoot = root.transform;
                Keep.Add(root);
            }
            var c = Object.Instantiate(src, _prefabRoot);
            c.name = d.Key + (src.name.Contains('_') ? src.name[src.name.IndexOf('_')..] : "");
            try { HeroModel.Build(d, c); } catch (Exception e) { Plugin.L.LogError($"{d.Name}: model in prefab '{c.name}': {e}"); }
            Keep.Add(c);
            return c;
        }

        static Sprite CloneSprite(Sprite s, string[] oldNames, string key)
        {
            Sprite c = null;
            try { c = Object.Instantiate(s); } catch (Exception e) { Plugin.L.LogWarning($"Sprite copy of '{s.name}': {e.Message}"); }
            if (c == null)
            {
                var rect = s.packed ? s.textureRect : s.rect;
                var pivot = new Vector2(s.pivot.x / s.rect.width, s.pivot.y / s.rect.height);
                c = Sprite.Create(s.texture, rect, pivot, s.pixelsPerUnit, 0, SpriteMeshType.FullRect, s.border);
            }
            c.name = SpriteName(s.name, oldNames, key);
            c.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Keep.Add(c);
            return c;
        }

        /// <summary>The template's sprite name with the template's names replaced by the hero's key.</summary>
        static string SpriteName(string n, string[] oldNames, string key)
        {
            bool renamed = false;
            foreach (var old in oldNames)
            {
                var r = Regex.Replace(n, Regex.Escape(old), key, RegexOptions.IgnoreCase);
                if (r != n) { n = r; renamed = true; }
            }
            return renamed ? n : key + "_" + n;
        }

        static Sprite LoadSprite(string path, string name)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var tex = Png.Decode(File.ReadAllBytes(path));
            tex.name = name;
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Keep.Add(tex);
            var s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            s.name = name;
            s.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Keep.Add(s);
            return s;
        }
    }
}
