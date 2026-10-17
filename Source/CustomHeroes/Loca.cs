// New localization entries (hero name, lore, ability texts). The game's text lookups go through LocaKey objects
// (key + table) into Unity Localization string tables, which load per language at runtime, so the entries are
// added to every loaded table of that collection (all languages get the English text) and re-checked every second.
// Some screens (the hero codex) load a table and read it in the same frame, before that check runs; for those, the
// "missing translation" hook below answers with our text and adds the entry on the spot.
using System;
using System.Collections.Generic;
using gg.leyline.unitylocalization;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;

namespace CustomHeroes
{
    internal static class Loca
    {
        sealed class Text { public string Table, Key, Value; }
        static readonly Dictionary<string, Text> Texts = new();
        static readonly Dictionary<IntPtr, int> Done = new();     // table -> number of our keys present
        static float _next;

        /// <summary>A LocaKey for a new text (null text = no key).</summary>
        public static LocaKey Key(string table, string key, string english)
        {
            if (english == null) return null;
            Texts[key] = new Text { Table = table, Key = key, Value = english };
            var k = new LocaKey(key, table);
            try { k._englishText = english; } catch (Exception e) { Plugin.L.LogWarning($"LocaKey {key}: english text: {e.Message}"); }
            try { k._localizedString = new LocalizedString(table, key); } catch (Exception e) { Plugin.L.LogWarning($"LocaKey {key}: LocalizedString: {e.Message}"); }
            _next = 0;
            return k;
        }

        public static string English(string key) => Texts.TryGetValue(key, out var t) ? t.Value : null;

        /// <summary>Unity Localization builds its "No translation found for 'key' in table" text in
        /// LocalizedStringDatabase.ProcessUntranslatedText; for our keys, return the text instead.</summary>
        public static void Patch(Harmony harmony)
        {
            var m = AccessTools.Method(typeof(LocalizedStringDatabase), "ProcessUntranslatedText");
            if (m == null) { Plugin.L.LogWarning("Loca: ProcessUntranslatedText not found; texts rely on the periodic add only"); return; }
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(Loca), nameof(Untranslated)));
        }

        static readonly HashSet<string> _servedLog = new();

        static bool Untranslated(string key, StringTable table, ref string __result)
        {
            try
            {
                if (key == null || !Texts.TryGetValue(key, out var t)) return true;
                __result = t.Value;
                if (table != null && table.SharedData != null && table.SharedData.TableCollectionName == t.Table && table.GetEntry(key) == null)
                    table.AddEntry(key, t.Value);
                if (_servedLog.Add(key)) Plugin.L.LogInfo($"Loca: '{key}' was read before it was added; served directly");
                return false;
            }
            catch (Exception e) { Plugin.L.LogWarning($"Loca: {key}: {e.Message}"); return true; }
        }

        public static void Tick()
        {
            if (Texts.Count == 0 || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            foreach (var t in Resources.FindObjectsOfTypeAll<StringTable>())
            {
                var shared = t.SharedData;
                if (shared == null) continue;
                string coll = shared.TableCollectionName;
                int present = 0, want = 0;
                foreach (var x in Texts.Values) if (x.Table == coll) want++;
                if (want == 0) continue;
                if (Done.TryGetValue(t.Pointer, out int n) && n == want && shared.Contains(LastKey(coll))) continue;
                int added = 0;
                foreach (var x in Texts.Values)
                {
                    if (x.Table != coll) continue;
                    try
                    {
                        var e = t.GetEntry(x.Key);
                        if (e == null) { t.AddEntry(x.Key, x.Value); added++; }
                        else if (e.Value != x.Value) e.Value = x.Value;
                        present++;
                    }
                    catch (Exception ex) { Plugin.L.LogWarning($"Loca: {x.Key} in '{t.name}': {ex.Message}"); }
                }
                Done[t.Pointer] = present;
                if (added > 0) Plugin.L.LogInfo($"Loca: added {added} entries to '{t.name}'");
            }
        }

        static string LastKey(string coll)
        {
            string last = null;
            foreach (var x in Texts.Values) if (x.Table == coll) last = x.Key;
            return last;
        }
    }
}
