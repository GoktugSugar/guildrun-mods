// Turns hero.json's compact effects into the game's own effect JSON (what its EffectSerializer and save
// files use), using IL2CPP reflection for field names, enum values and fixed-point (FP) fields:
//
//   { "Trigger": "OnAbilityDamageDealt", "Target": "TriggerTarget", "Attach": "OwnerIsSource",
//     "Actions": [ { "Type": "ApplyStatusWithDurationAction", "Status": "Stun", "Amount": 1, "Duration": 1.5 } ] }
//
// becomes {"$type":"Ember.Balancing.SimulationBridge.Effects.ModularEffect, ember.balancing","TargetType":6,...}.
// Effects already in the game's format (full "$type" names, k__BackingField keys) pass through unchanged.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime;

namespace CustomHeroes
{
    internal static unsafe class EffectJson
    {
        const string Lib = "GameAssembly";
        [DllImport(Lib)] static extern IntPtr il2cpp_class_get_fields(IntPtr klass, ref IntPtr iter);
        [DllImport(Lib)] static extern IntPtr il2cpp_field_get_name(IntPtr field);
        [DllImport(Lib)] static extern IntPtr il2cpp_field_get_type(IntPtr field);
        [DllImport(Lib)] static extern int il2cpp_field_get_flags(IntPtr field);
        [DllImport(Lib)] static extern IntPtr il2cpp_class_from_type(IntPtr type);
        [DllImport(Lib)] static extern IntPtr il2cpp_type_get_name(IntPtr type);
        [DllImport(Lib)] static extern int il2cpp_type_get_type(IntPtr type);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] static extern bool il2cpp_class_is_enum(IntPtr klass);
        [DllImport(Lib)] static extern void il2cpp_field_static_get_value(IntPtr field, void* value);
        [DllImport(Lib)] static extern IntPtr il2cpp_class_get_element_class(IntPtr klass);
        [DllImport(Lib)] static extern IntPtr il2cpp_class_get_namespace(IntPtr klass);
        [DllImport(Lib)] static extern IntPtr il2cpp_class_get_name(IntPtr klass);
        [DllImport(Lib)] static extern IntPtr il2cpp_class_get_assemblyname(IntPtr klass);
        [DllImport(Lib)] static extern IntPtr il2cpp_class_get_parent(IntPtr klass);
        [DllImport(Lib)] static extern IntPtr il2cpp_class_get_declaring_type(IntPtr klass);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] static extern bool il2cpp_class_is_interface(IntPtr klass);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] static extern bool il2cpp_class_is_abstract(IntPtr klass);

        const int TypeSzArray = 0x1d, FieldStatic = 0x10;

        // where the game keeps effect actions, conditions and ability actions
        static readonly (string image, string ns)[] Spaces =
        {
            ("ember.balancing.dll", "Ember.Balancing.SimulationBridge.Effects.Actions"),
            ("ember.simulation.core.dll", "Ember.Simulation.Core.State.Effects.Actions"),
            ("ember.balancing.dll", "Ember.Balancing.SimulationBridge.Effects.Conditions"),
            ("ember.simulation.core.dll", "Ember.Simulation.Core.State.Effects.Conditions"),
            ("ember.simulation.core.dll", "Ember.Simulation.Core.State.Effects.Conditions.Targeting"),
            ("ember.balancing.dll", "Ember.Balancing.SimulationBridge.Effects"),
            ("ember.balancing.dll", "Ember.Balancing.SimulationBridge.Common"),
            ("ember.balancing.dll", "Ember.Balancing.SimulationBridge.AbilityActions"),
            ("ember.simulation.core.dll", "Ember.Simulation.Core.Fsm.States.ActiveAbilities"),
            ("ember.simulation.core.dll", "Ember.Simulation.Core.Fsm.States.ActiveAbilities.Timing"),
            ("ember.simulation.core.dll", "Ember.Simulation.Core.Fsm.States.ActiveAbilities.Targeting"),
            ("ember.simulation.core.dll", "Ember.Simulation.Core.Fsm.States.ActiveAbilities.Movement"),
        };

        // friendly ModularEffect keys -> field names
        static readonly Dictionary<string, string> EffectKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Trigger"] = "<Trigger>k__BackingField", ["Target"] = "TargetType", ["Class"] = "ClassId", ["Guild"] = "GuildId",
            ["Condition"] = "_condition", ["Actions"] = "_actions", ["Attach"] = "<AttachmentType>k__BackingField",
            ["AttachClass"] = "<AttachmentClassId>k__BackingField", ["Increment"] = "<TriggerIncrement>k__BackingField",
            ["Interval"] = "<EffectInterval>k__BackingField", ["Keyword"] = "<Keyword>k__BackingField",
            ["Rush"] = "<RushDuration>k__BackingField", ["Stall"] = "<StallDuration>k__BackingField",
            ["Projectile"] = "<ProjectileType>k__BackingField",
        };

        public static Func<string, int> ClassOrGuildId;   // set by the injector

        static readonly Dictionary<string, (string full, IntPtr klass)> TypeCache = new(StringComparer.OrdinalIgnoreCase);

        public static IntPtr ClassOf(string shortName) => Resolve(shortName).klass;

        static (string full, IntPtr klass) Resolve(string name)
        {
            if (TypeCache.TryGetValue(name, out var hit)) return hit;
            string ns = null, cls = name;
            int dot = name.LastIndexOf('.');
            if (dot > 0) { ns = name[..dot]; cls = name[(dot + 1)..]; }
            foreach (var (image, space) in Spaces)
            {
                if (ns != null && ns != space) continue;
                var k = IL2CPP.GetIl2CppClass(image, space, cls);
                if (k == IntPtr.Zero) continue;
                var asm = Marshal.PtrToStringAnsi(il2cpp_class_get_assemblyname(k));
                return TypeCache[name] = ($"{space}.{cls}, {asm}", k);
            }
            throw new Exception($"unknown effect type '{name}'");
        }

        /// <summary>Class of a "$type" name ("Namespace.Class, assembly", or a short name from the effect namespaces).</summary>
        public static IntPtr ClassOfTypeName(string full)
        {
            var parts = full.Split(',');
            string name = parts[0].Trim(), asm = parts.Length > 1 ? parts[1].Trim() : null;
            int dot = name.LastIndexOf('.');
            if (asm != null && dot > 0)
            {
                var k = IL2CPP.GetIl2CppClass(asm + ".dll", name[..dot], name[(dot + 1)..]);
                if (k != IntPtr.Zero) return k;
            }
            return Resolve(name).klass;
        }

        /// <summary>"Namespace.Class, assembly" as Newtonsoft writes it (nested classes as Outer+Inner).</summary>
        static string FullName(IntPtr klass)
        {
            string name = Str(il2cpp_class_get_name(klass));
            var k = klass;
            for (var outer = il2cpp_class_get_declaring_type(k); outer != IntPtr.Zero; outer = il2cpp_class_get_declaring_type(k))
            {
                name = Str(il2cpp_class_get_name(outer)) + "+" + name;
                k = outer;
            }
            var ns = Str(il2cpp_class_get_namespace(k));
            return $"{(string.IsNullOrEmpty(ns) ? "" : ns + ".")}{name}, {Str(il2cpp_class_get_assemblyname(klass))}";
        }

        static string Str(IntPtr p) => Marshal.PtrToStringAnsi(p);

        /// <summary>Instance field of klass (or a base class) matching name, its backing field, or any case.</summary>
        static (string name, IntPtr type) Field(IntPtr klass, string name)
        {
            var (fn, type, _) = FieldInfo(klass, name);
            return (fn, type);
        }

        static (string name, IntPtr type, IntPtr field) FieldInfo(IntPtr klass, string name)
        {
            for (var k = klass; k != IntPtr.Zero; k = il2cpp_class_get_parent(k))
            {
                var iter = IntPtr.Zero; IntPtr f;
                (string, IntPtr, IntPtr) loose = (null, IntPtr.Zero, IntPtr.Zero);
                while ((f = il2cpp_class_get_fields(k, ref iter)) != IntPtr.Zero)
                {
                    if ((il2cpp_field_get_flags(f) & FieldStatic) != 0) continue;
                    var fn = Str(il2cpp_field_get_name(f));
                    if (fn == name || fn == $"<{name}>k__BackingField") return (fn, il2cpp_field_get_type(f), f);
                    if (loose.Item1 == null && (string.Equals(fn, name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(fn, $"<{name}>k__BackingField", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(fn, "_" + name, StringComparison.OrdinalIgnoreCase)))
                        loose = (fn, il2cpp_field_get_type(f), f);
                }
                if (loose.Item1 != null) return loose;
            }
            return (null, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Sets a number, FP, bool or enum field of a live IL2CPP object (hero.json "ActionFields").
        /// False if the object has no such field.</summary>
        public static bool SetField(IntPtr obj, string name, JsonElement v)
        {
            var (fname, type, field) = FieldInfo(Native.il2cpp_object_get_class(obj), name);
            if (fname == null) return false;
            var p = obj + Native.il2cpp_field_get_offset(field);
            var tname = Str(il2cpp_type_get_name(type));
            var klass = il2cpp_class_from_type(type);
            switch (tname)
            {
                case "Photon.Deterministic.FP": *(long*)p = (long)Math.Round(Num(v) * 65536.0); break;
                case "System.Int32": *(int*)p = (int)Math.Round(Num(v)); break;
                case "System.Single": *(float*)p = (float)Num(v); break;
                case "System.Boolean": *(byte*)p = (byte)(v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetDouble() != 0) ? 1 : 0); break;
                default:
                    if (klass == IntPtr.Zero || !il2cpp_class_is_enum(klass))
                        throw new Exception($"{fname} is a {tname}; only numbers, FP, bools and enums can be set");
                    *(int*)p = v.ValueKind == JsonValueKind.String ? EnumValue(klass, v.GetString()) : (int)Num(v);
                    break;
            }
            return true;
        }

        static double Num(JsonElement v) => v.ValueKind == JsonValueKind.String ? double.Parse(v.GetString(), CultureInfo.InvariantCulture) : v.GetDouble();

        static string FpText(double v) => v.ToString("0.00####", CultureInfo.InvariantCulture);

        static int EnumValue(IntPtr enumClass, string name)
        {
            var iter = IntPtr.Zero; IntPtr f;
            while ((f = il2cpp_class_get_fields(enumClass, ref iter)) != IntPtr.Zero)
            {
                if ((il2cpp_field_get_flags(f) & FieldStatic) == 0) continue;
                if (!string.Equals(Str(il2cpp_field_get_name(f)), name, StringComparison.OrdinalIgnoreCase)) continue;
                long v = 0;
                il2cpp_field_static_get_value(f, &v);
                return (int)v;
            }
            throw new Exception($"'{name}' is not a value of {Str(il2cpp_class_get_name(enumClass))}");
        }

        static bool LooksNative(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name.EndsWith("k__BackingField")) return true;
                if (p.Name == "$type" && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString().Contains(',')) return true;
            }
            return false;
        }

        /// <summary>A hero.json effect as the game's effects: one, or two for "Backup": true (works in play and in
        /// reserve). The game builds Backup the same way: a ReserveOnlyModularEffect copy, priority 10 unless given.</summary>
        public static List<string> Effects(JsonElement e)
        {
            var list = new List<string> { Effect(e) };
            if (!LooksNative(e) && e.TryGetProperty("Backup", out var b) && b.ValueKind == JsonValueKind.True)
                list.Add(Effect(e, "ReserveOnlyModularEffect", e.TryGetProperty("Priority", out var p) ? p.GetInt32() : 10));
            return list;
        }

        /// <summary>The class named by an effect JSON's "$type" (ModularEffect if none).</summary>
        public static IntPtr EffectClass(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("$type", out var t) ? ClassOfTypeName(t.GetString()) : Resolve("ModularEffect").klass;
        }

        /// <summary>One effect (compact or native) as the game's JSON. "Type" picks the effect class (default
        /// ModularEffect; "Priority" alone means PriorityModularEffect, which runs in priority order).</summary>
        public static string Effect(JsonElement e, string asType = null, int? priority = null)
        {
            if (LooksNative(e)) return e.GetRawText();
            string typeName = asType;
            if (typeName == null && e.TryGetProperty("Type", out var tp)) typeName = tp.GetString();
            if (typeName == null && e.TryGetProperty("Priority", out _)) typeName = "PriorityModularEffect";
            var (full, klass) = Resolve(typeName ?? "ModularEffect");
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("$type", full);
                if (priority != null) w.WriteNumber(Field(klass, "Priority").name ?? "_priority", priority.Value);
                bool hasCond = false, hasKeyword = false, hasInc = false;
                double rush = 0, stall = 0;
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == "Type" || p.Name == "Backup" || (p.Name == "Priority" && priority != null)) continue;
                    if (!EffectKeys.TryGetValue(p.Name, out var field)) field = p.Name;
                    if (field == "_condition") hasCond = true;
                    if (field == "<Keyword>k__BackingField") hasKeyword = true;
                    if (field == "<TriggerIncrement>k__BackingField") hasInc = true;
                    if (field == "<RushDuration>k__BackingField" && p.Value.ValueKind == JsonValueKind.Number) rush = p.Value.GetDouble();
                    if (field == "<StallDuration>k__BackingField" && p.Value.ValueKind == JsonValueKind.Number) stall = p.Value.GetDouble();
                    if ((field == "ClassId" || field == "GuildId" || field == "<AttachmentClassId>k__BackingField") && p.Value.ValueKind == JsonValueKind.String)
                    {
                        w.WriteNumber(field, ClassOrGuildId(p.Value.GetString()));
                        continue;
                    }
                    var (fname, ftype) = Field(klass, field);
                    if (fname == null) throw new Exception($"{Str(il2cpp_class_get_name(klass))} has no field '{p.Name}'");
                    w.WritePropertyName(fname);
                    Value(w, p.Value, ftype);
                }
                if (!hasCond)
                {
                    w.WritePropertyName("_condition");
                    w.WriteStartObject(); w.WriteString("$type", Resolve("NoCondition").full); w.WriteEndObject();
                }
                if (!hasInc) w.WriteNumber("<TriggerIncrement>k__BackingField", 1);
                if (!hasKeyword && (rush > 0 || stall > 0)) w.WriteNumber("<Keyword>k__BackingField", rush > 0 ? 1 : 2);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        /// <summary>A compact object ({"Type": "...", fields}) as the game's JSON, or null for a native one.</summary>
        public static string Object(JsonElement e, string defaultType = null)
        {
            if (LooksNative(e)) return e.GetRawText();
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms)) Obj(w, e, defaultType != null ? Resolve(defaultType).klass : IntPtr.Zero);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        static void Obj(Utf8JsonWriter w, JsonElement e, IntPtr declared)
        {
            if (e.ValueKind == JsonValueKind.String)   // "NoCondition" shorthand
            {
                w.WriteStartObject(); w.WriteString("$type", Resolve(e.GetString()).full); w.WriteEndObject();
                return;
            }
            if (LooksNative(e)) { w.WriteRawValue(e.GetRawText()); return; }
            var klass = declared;
            string typeName = null;
            foreach (var p in e.EnumerateObject())
                if (p.Name == "Type" || p.Name == "$type") typeName = p.Value.GetString();
            if (typeName == null && IsEffectSlot(declared, e))
            {   // an effect inside an ability action or another effect (DelayedEffectBlock.Effects...)
                w.WriteRawValue(Effect(e));
                return;
            }
            w.WriteStartObject();
            if (typeName != null)
            {
                var r = Resolve(typeName);
                klass = r.klass;
                w.WriteString("$type", r.full);
            }
            else if (klass != IntPtr.Zero && !il2cpp_class_is_interface(klass) && !il2cpp_class_is_abstract(klass))
                w.WriteString("$type", FullName(klass));
            if (klass == IntPtr.Zero) throw new Exception("object without \"Type\": " + e.GetRawText());
            bool scaling = Str(il2cpp_class_get_name(klass)) == "DamageScaling";
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name == "Type" || p.Name == "$type" || p.Name == "$id") continue;
                if (scaling && p.Name == "Factors")
                {   // "Factors": [C, B, A, S] (one value = every rank)
                    var f = new List<double>();
                    if (p.Value.ValueKind == JsonValueKind.Array) foreach (var x in p.Value.EnumerateArray()) f.Add(x.GetDouble());
                    else f.Add(p.Value.GetDouble());
                    for (int r = 0; r < 4; r++) w.WriteString($"Rank{r + 1}Factor", FpText(f.Count == 0 ? 0 : f[Math.Min(r, f.Count - 1)]));
                    continue;
                }
                var (fname, ftype) = Field(klass, p.Name);
                if (fname == null) throw new Exception($"{Str(il2cpp_class_get_name(klass))} has no field '{p.Name}'");
                w.WritePropertyName(fname);
                if (p.Value.ValueKind == JsonValueKind.String && (fname.Contains("Class") || fname.Contains("Guild"))
                    && Str(il2cpp_type_get_name(ftype)) == "System.Int32")
                {   // class / guild by name in conditions ("ClassId1": "Vanguard")
                    w.WriteNumberValue(ClassOrGuildId(p.Value.GetString()));
                    continue;
                }
                Value(w, p.Value, ftype);
            }
            w.WriteEndObject();
        }

        /// <summary>Compact effect where the game expects an effect (an IEffect / ModularEffect slot, or an object
        /// that has Trigger and Actions).</summary>
        static bool IsEffectSlot(IntPtr declared, JsonElement e)
        {
            if (declared != IntPtr.Zero)
            {
                var n = Str(il2cpp_class_get_name(declared));
                if (n == "IEffect" || n.EndsWith("ModularEffect")) return true;
            }
            return e.TryGetProperty("Trigger", out _) && e.TryGetProperty("Actions", out _);
        }

        static void Value(Utf8JsonWriter w, JsonElement v, IntPtr type)
        {
            var tname = type == IntPtr.Zero ? "" : Str(il2cpp_type_get_name(type));
            var klass = type == IntPtr.Zero ? IntPtr.Zero : il2cpp_class_from_type(type);
            if (tname == "Photon.Deterministic.FP")
            {   // the game writes fixed-point numbers as strings
                if (v.ValueKind == JsonValueKind.Number) w.WriteStringValue(FpText(v.GetDouble()));
                else v.WriteTo(w);
                return;
            }
            if (klass != IntPtr.Zero && il2cpp_class_is_enum(klass))
            {
                if (v.ValueKind == JsonValueKind.String) w.WriteNumberValue(EnumValue(klass, v.GetString()));
                else v.WriteTo(w);
                return;
            }
            if (type != IntPtr.Zero && il2cpp_type_get_type(type) == TypeSzArray && v.ValueKind == JsonValueKind.Array)
            {
                var elem = il2cpp_class_get_element_class(klass);
                var elemType = Field_TypeOfClass(elem);
                w.WriteStartArray();
                foreach (var x in v.EnumerateArray())
                {
                    if (x.ValueKind == JsonValueKind.Object || (x.ValueKind == JsonValueKind.String && !IsPrimitiveName(elem)))
                        Obj(w, x, elem);
                    else Value(w, x, elemType);
                }
                w.WriteEndArray();
                return;
            }
            if (v.ValueKind == JsonValueKind.Object || (v.ValueKind == JsonValueKind.String && klass != IntPtr.Zero && !IsPrimitiveName(klass) && tname != "System.String"))
            {
                Obj(w, v, klass);
                return;
            }
            v.WriteTo(w);
        }

        [DllImport(Lib)] static extern IntPtr il2cpp_class_get_type(IntPtr klass);
        static IntPtr Field_TypeOfClass(IntPtr klass) => klass == IntPtr.Zero ? IntPtr.Zero : il2cpp_class_get_type(klass);

        static bool IsPrimitiveName(IntPtr klass)
        {
            var ns = Str(il2cpp_class_get_namespace(klass));
            var n = Str(il2cpp_class_get_name(klass));
            return ns == "System" && n is "String" or "Int32" or "Int64" or "Single" or "Double" or "Boolean" or "Byte";
        }
    }
}
