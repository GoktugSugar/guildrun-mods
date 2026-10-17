// Custom heroes' voice lines (hero.json "Voice": a folder with one subfolder of clips per slot). The game builds
// a hero's voice events from its key (event:/SFX/COMB/HEROES/Freddy/Freddy_skill_vo), which the game's banks don't
// have. Harmony prefixes on FMODAudioService.PlayOneShot / TryStartEvent catch those paths (key and slot as whole
// tokens) and play a random clip of that slot through FMOD core on the voice bus instead.
// (A native detour on FMOD_Studio_EventInstance_Start crashed coreclr, so stay on the managed side.)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using Ember.Scopes.Application.Audio.Services;
using HarmonyLib;
using UnityEngine;

namespace CustomHeroes
{
    internal static class Voice
    {
        const string Dll = "fmodstudio";
        [DllImport(Dll)] static extern int FMOD_Studio_System_GetCoreSystem(IntPtr system, out IntPtr core);
        [StructLayout(LayoutKind.Sequential)] struct Guid16 { public int A, B, C, D; }
        [DllImport(Dll)] static extern int FMOD_Studio_System_GetEventByID(IntPtr system, ref Guid16 id, out IntPtr desc);
        [DllImport(Dll)] static extern int FMOD_Studio_EventDescription_GetPath(IntPtr desc, byte[] path, int size, out int retrieved);
        [DllImport(Dll)] static extern int FMOD_Studio_System_GetBankCount(IntPtr system, out int count);
        [DllImport(Dll)] static extern int FMOD_Studio_System_GetBankList(IntPtr system, IntPtr[] banks, int capacity, out int count);
        [DllImport(Dll)] static extern int FMOD_Studio_Bank_GetBusCount(IntPtr bank, out int count);
        [DllImport(Dll)] static extern int FMOD_Studio_Bank_GetBusList(IntPtr bank, IntPtr[] buses, int capacity, out int count);
        [DllImport(Dll)] static extern int FMOD_Studio_Bus_GetPath(IntPtr bus, byte[] path, int size, out int retrieved);
        [DllImport(Dll)] static extern int FMOD_Studio_Bus_LockChannelGroup(IntPtr bus);
        [DllImport(Dll)] static extern int FMOD_Studio_Bus_GetChannelGroup(IntPtr bus, out IntPtr group);
        [DllImport(Dll)] static extern int FMOD5_System_CreateSound(IntPtr core, byte[] name, uint mode, IntPtr exinfo, out IntPtr sound);
        [DllImport(Dll)] static extern int FMOD5_System_PlaySound(IntPtr core, IntPtr sound, IntPtr group, bool paused, out IntPtr channel);
        [DllImport(Dll)] static extern int FMOD5_System_GetMasterChannelGroup(IntPtr core, out IntPtr group);
        [DllImport(Dll)] static extern int FMOD5_Channel_SetVolume(IntPtr channel, float volume);


        static readonly Dictionary<string, string> SlotOf = new();      // event path -> "HERO|SLOT" key (null = not ours)
        static readonly Dictionary<string, string[]> Clips = new();     // "HERO|SLOT" -> clip files
        static List<HeroDefinition> _heroes = new();
        static readonly Dictionary<string, IntPtr> Sounds = new();
        static readonly Dictionary<string, int> LastPick = new();
        static readonly System.Random Rng = new();
        static IntPtr _core;
        static readonly List<(string path, IntPtr bus)> Buses = new();
        static readonly Dictionary<IntPtr, IntPtr> Groups = new();   // bus -> locked channel group
        static bool _busSearched;

        public static void Init(Harmony harmony, List<HeroDefinition> heroes)
        {
            _heroes = heroes;
            foreach (var h in heroes)
            {
                var dir = h.Path(h.Voice);
                if (dir == null || !Directory.Exists(dir)) continue;
                foreach (var d in Directory.GetDirectories(dir))
                {
                    var files = Directory.GetFiles(d).Where(f => f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (files.Length > 0) Clips[h.Key.ToUpperInvariant() + "|" + Path.GetFileName(d).ToUpperInvariant()] = files;
                }
            }
            if (Clips.Count == 0) { Plugin.L.LogInfo("Voice: no hero has voice clips, voice off."); return; }
            var oneShot = AccessTools.Method(typeof(FMODAudioService), "PlayOneShot", new[] { typeof(string), typeof(Vector3) });
            harmony.Patch(oneShot, prefix: new HarmonyMethod(typeof(Voice), nameof(PlayOneShotPrefix)));
            var rankUp = AccessTools.Method(typeof(Ember.Scopes.GameRun.Audio.FMODBattleAudioService), "PlayRankUpVoiceOver");
            if (rankUp != null) harmony.Patch(rankUp, prefix: new HarmonyMethod(typeof(Voice), nameof(RankUpPrefix)));
            var refType = typeof(FMODUnity.EventReference);
            var oneShotRef = AccessTools.Method(typeof(FMODAudioService), "PlayOneShot", new[] { refType, typeof(Vector3) });
            if (oneShotRef != null) harmony.Patch(oneShotRef, prefix: new HarmonyMethod(typeof(Voice), nameof(PlayOneShotRefPrefix)));
            var startRef = AccessTools.Method(typeof(FMODAudioService), "TryStartEvent", new[] { refType });
            if (startRef != null) harmony.Patch(startRef, prefix: new HarmonyMethod(typeof(Voice), nameof(TryStartRefPrefix)));
            var create = AccessTools.Method(typeof(FMODAudioService), "TryCreateEvent", new[] { typeof(string), typeof(FMOD.Studio.EventInstance).MakeByRefType() });
            if (create != null) harmony.Patch(create, prefix: new HarmonyMethod(typeof(Voice), nameof(TryCreatePrefix)));
            Plugin.L.LogInfo($"Voice: patched FMODAudioService; {Clips.Count} slots, {Clips.Values.Sum(v => v.Length)} clips.");
        }

        // Rank-up VO is one event for every rank; remember which rank so the matching line plays.
        static int _pendingRank = -1;
        static float _pendingRankTime;
        static void RankUpPrefix(int newRank)
        {
            _pendingRank = newRank;
            _pendingRankTime = Time.unscaledTime;
            Plugin.L.LogInfo($"Voice: rank up to {newRank}");
        }

        static string RankLetter(int rank) => rank switch { 0 => "C", 1 => "B", 2 => "A", _ => "S" };

        static bool PlayOneShotPrefix(string eventPath)
        {
            try { if (Slot(eventPath, "PlayOneShot") is string slot && Play(slot)) return false; }
            catch (Exception e) { Plugin.L.LogError("Voice: " + e); }
            return true;
        }

        // Events referenced by GUID: look up the path, then treat them like PlayOneShot(path).
        static readonly Dictionary<(int, int, int, int), string> RefPaths = new();
        static string PathOf(FMODUnity.EventReference reference)
        {
            if (reference == null) return null;
            var g = reference.Guid;
            var k = (g.Data1, g.Data2, g.Data3, g.Data4);
            if (RefPaths.TryGetValue(k, out var p)) return p;
            p = null;
            var id = new Guid16 { A = g.Data1, B = g.Data2, C = g.Data3, D = g.Data4 };
            if (FMOD_Studio_System_GetEventByID(FMODUnity.RuntimeManager.StudioSystem.handle, ref id, out var desc) == 0)
            {
                var b = new byte[512];
                if (FMOD_Studio_EventDescription_GetPath(desc, b, b.Length, out int len) == 0) p = Encoding.UTF8.GetString(b, 0, Math.Max(0, len - 1));
            }
            RefPaths[k] = p;
            return p;
        }

        static bool PlayOneShotRefPrefix(FMODUnity.EventReference reference)
        {
            try { if (Slot(PathOf(reference), "PlayOneShot") is string slot && Play(slot)) return false; }
            catch (Exception e) { Plugin.L.LogError("Voice: " + e); }
            return true;
        }

        static bool TryStartRefPrefix(FMODUnity.EventReference reference, ref bool __result)
        {
            try { if (Slot(PathOf(reference), "PlayOneShot") is string slot && Play(slot)) { __result = true; return false; } }
            catch (Exception e) { Plugin.L.LogError("Voice: " + e); }
            return true;
        }

        // Only logs, so we can see if hero VO is ever played through a held event instance.
        static void TryCreatePrefix(string eventPath)
        {
            try { Slot(eventPath, "TryCreateEvent"); } catch { }
        }

        static string Slot(string path, string via)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (SlotOf.TryGetValue(path, out var key)) return via == "PlayOneShot" ? key : null;
            var norm = "_" + new string(path.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()) + "_";
            key = null;
            var def = _heroes.FirstOrDefault(h => norm.Contains("_" + h.Key.ToUpperInvariant() + "_"));
            if (def != null)
            {
                var prefix = def.Key.ToUpperInvariant() + "|";
                var slots = Clips.Keys.Where(k => k.StartsWith(prefix)).Select(k => k[prefix.Length..]).OrderByDescending(k => k.Length).ToArray();
                var squashed = norm.Replace("_", "");
                var slot = slots.FirstOrDefault(sl => norm.Contains("_" + sl + "_"))
                    ?? slots.FirstOrDefault(sl => sl.Length >= 6 && squashed.Contains(sl.Replace("_", "")));   // rank_up_vo == RANKUP_VO
                if (slot != null) key = prefix + slot;
                Plugin.L.LogInfo($"Voice: {via} '{path}' -> {(key != null && via == "PlayOneShot" ? key : key != null ? key + " (not replaced: held instance)" : "kept original")}");
            }
            else if (path.EndsWith("_vo", StringComparison.OrdinalIgnoreCase) || norm.Contains("_VO_"))
                Plugin.L.LogInfo($"Voice: {via} '{path}' (voice event, not a custom hero's)");
            SlotOf[path] = key;
            return via == "PlayOneShot" ? key : null;
        }

        static bool Play(string slot)
        {
            if (_core == IntPtr.Zero)
            {
                var studio = FMODUnity.RuntimeManager.StudioSystem.handle;
                if (FMOD_Studio_System_GetCoreSystem(studio, out _core) != 0) return false;
                FindBus(studio);
            }
            var files = Clips[slot];
            int pick = Rng.Next(files.Length);
            if (_pendingRank >= 0 && Time.unscaledTime - _pendingRankTime < 1f)
            {
                var tag = "rank_" + RankLetter(_pendingRank) + "__";
                int ranked = Array.FindIndex(files, f => Path.GetFileName(f).StartsWith(tag, StringComparison.OrdinalIgnoreCase));
                if (ranked < 0 && files.Any(f => Path.GetFileName(f).StartsWith("rank_", StringComparison.OrdinalIgnoreCase)))
                    ranked = Array.FindIndex(files, f => Path.GetFileName(f).StartsWith("rank_S__", StringComparison.OrdinalIgnoreCase));
                if (ranked >= 0) { pick = ranked; _pendingRank = -1; goto picked; }
            }
            if (files.Length > 1 && LastPick.TryGetValue(slot, out int last) && pick == last) pick = (pick + 1) % files.Length;
        picked:
            LastPick[slot] = pick;
            var file = files[pick];
            if (!Sounds.TryGetValue(file, out var sound))
            {
                const uint CREATESAMPLE = 0x100, MODE_2D = 0x8;
                int r = FMOD5_System_CreateSound(_core, Encoding.UTF8.GetBytes(file + "\0"), CREATESAMPLE | MODE_2D, IntPtr.Zero, out sound);
                if (r != 0) { Plugin.L.LogWarning($"Voice: could not load {file} (FMOD error {r})"); return false; }
                Sounds[file] = sound;
            }
            var group = GroupFor(slot);
            if (group == IntPtr.Zero) FMOD5_System_GetMasterChannelGroup(_core, out group);
            if (FMOD5_System_PlaySound(_core, sound, group, false, out var ch) != 0) return false;
            FMOD5_Channel_SetVolume(ch, Plugin.VoiceVolume.Value);
            return true;
        }

        // Combat lines go on the combat VO bus, shop/run lines on the shop VO bus (the game mixes them differently).
        static IntPtr GroupFor(string slot)
        {
            var want = Plugin.VoiceBus.Value.Trim();
            slot = slot[(slot.IndexOf('|') + 1)..];
            bool combat = slot.Contains("SKILL") || slot.Contains("DEATH") || slot.StartsWith("AA_") || slot == "OD" || slot.Contains("KILL");
            var pick = Buses.FirstOrDefault(b => want.Length > 0 && b.path.Equals(want, StringComparison.OrdinalIgnoreCase));
            if (pick.bus == IntPtr.Zero) pick = Buses.FirstOrDefault(b => b.path.Equals(combat ? "bus:/VO/VO_COMB" : "bus:/VO/VO_SHOP", StringComparison.OrdinalIgnoreCase));
            if (pick.bus == IntPtr.Zero) pick = Buses.FirstOrDefault(b => b.path.StartsWith("bus:/VO", StringComparison.OrdinalIgnoreCase));
            if (pick.bus == IntPtr.Zero) return IntPtr.Zero;
            if (!Groups.TryGetValue(pick.bus, out var g) || g == IntPtr.Zero)
            {
                if (FMOD_Studio_Bus_GetChannelGroup(pick.bus, out g) != 0) { FMOD_Studio_Bus_LockChannelGroup(pick.bus); g = IntPtr.Zero; }
                Groups[pick.bus] = g;
                if (g != IntPtr.Zero) Plugin.L.LogInfo($"Voice: {slot} plays on {pick.path}");
            }
            return g;
        }

        static void FindBus(IntPtr studio)
        {
            if (_busSearched) return;
            _busSearched = true;
            var want = Plugin.VoiceBus.Value.Trim();
            var all = new List<(string path, IntPtr bus)>();
            FMOD_Studio_System_GetBankCount(studio, out int nb);
            var banks = new IntPtr[Math.Max(nb, 1)];
            FMOD_Studio_System_GetBankList(studio, banks, banks.Length, out nb);
            for (int i = 0; i < nb; i++)
            {
                FMOD_Studio_Bank_GetBusCount(banks[i], out int n);
                if (n <= 0) continue;
                var buses = new IntPtr[n];
                FMOD_Studio_Bank_GetBusList(banks[i], buses, n, out n);
                for (int j = 0; j < n; j++)
                {
                    var b = new byte[512];
                    if (FMOD_Studio_Bus_GetPath(buses[j], b, b.Length, out int len) == 0)
                        all.Add((Encoding.UTF8.GetString(b, 0, Math.Max(0, len - 1)), buses[j]));
                }
            }
            Buses.AddRange(all.GroupBy(x => x.path).Select(g => g.First()));
            Plugin.L.LogInfo("Voice: buses: " + string.Join(", ", Buses.Select(x => x.path)));
            foreach (var bus in Buses.Where(x => x.path.StartsWith("bus:/VO", StringComparison.OrdinalIgnoreCase)))
                FMOD_Studio_Bus_LockChannelGroup(bus.bus);   // channel groups become available after the next studio update
        }
    }
}
