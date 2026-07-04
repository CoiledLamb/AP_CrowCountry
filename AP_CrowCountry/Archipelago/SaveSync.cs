using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json;
using com.sfbgames.playmaker;
using APCrowCountry.Utils;

namespace APCrowCountry.Archipelago;

/// <summary>
/// Binds AP progress to the game's save slots. The game snapshots all
/// PlayMaker globals into Saves/saveN.sav; we pair each slot with a sidecar
/// JSON recording which received items are already baked into that snapshot
/// (appliedIndex), which locations were checked, and which multiworld the
/// run belongs to. Both files are written at the same SaveToSlot moment, so
/// they can never disagree: reloading a save rolls the item counters and the
/// applied index back together, and the delta re-applies.
/// </summary>
public static class SaveSync
{
    // ---- current globals-timeline state ----
    public static bool TimelineActive;   // a run (new game or loaded save) is live
    public static int CurrentSlot;       // save slot the run is bound to; 0 until first save
    public static int AppliedIndex;      // received items [0, AppliedIndex) are baked into the globals
    public static bool SeedMismatch;     // loaded save belongs to a different multiworld: AP disabled
    public static string RunSeed;        // seed this run belongs to (null/empty = adopt the session's)
    public static string RunSlotName;

    public static bool CanApplyItems => TimelineActive && !SeedMismatch;

    private class Sidecar
    {
        public string seed;
        public string slotName;
        public int appliedIndex;
        public List<long> checkedLocations = new();
    }

    // maps a save's globals byte[] back to the slot LoadFromSlot read it
    // from, so BytesToGlobals can be attributed (PMLoadLatestSlot probes all
    // three slots before applying one)
    private static readonly ConditionalWeakTable<byte[], object> bytesSource = new();

    // game code uses CWD-relative "Saves/" paths; do the same
    private static string SidecarPath(int slot) => Path.Combine("Saves", $"ap_slot{slot}.json");

    [HarmonyPatch(typeof(CrowCountrySave), nameof(CrowCountrySave.LoadFromSlot))]
    static class CrowCountrySave_LoadFromSlot
    {
        static void Postfix(int slotID, CrowCountrySaveSlot __result)
        {
            if (__result?.globalVarsBytes == null) return;
            bytesSource.Remove(__result.globalVarsBytes);
            bytesSource.Add(__result.globalVarsBytes, slotID);
        }
    }

    [HarmonyPatch(typeof(CrowCountrySave), nameof(CrowCountrySave.BytesToGlobals))]
    static class CrowCountrySave_BytesToGlobals
    {
        static void Postfix(byte[] bytes, bool doSet)
        {
            if (!doSet) return; // read-only peek (e.g. crystal crow counts)
            try
            {
                if (bytes != null && bytesSource.TryGetValue(bytes, out object slot))
                {
                    // slot 0 is the settings/unlocks overlay, not a timeline event
                    if ((int)slot >= 1) OnGameplayLoaded((int)slot);
                }
                else
                {
                    // bytes that never came from a slot are the baked blank-
                    // gameplay TextAsset: the New Game globals reset
                    OnTimelineReset();
                }
            }
            catch (Exception e)
            {
                Plugin.BepinLogger.LogError($"SaveSync load hook failed: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(CrowCountrySave), nameof(CrowCountrySave.SaveToSlot))]
    static class CrowCountrySave_SaveToSlot
    {
        static void Postfix(int slotID, bool __result)
        {
            if (!__result || slotID < 1) return; // slot 0 = settings/unlocks
            try
            {
                OnGameplaySaved(slotID);
            }
            catch (Exception e)
            {
                Plugin.BepinLogger.LogError($"SaveSync save hook failed: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(CrowCountrySave), nameof(CrowCountrySave.DeleteSlot))]
    static class CrowCountrySave_DeleteSlot
    {
        static void Postfix(int slotID, bool __result)
        {
            if (!__result) return;
            try
            {
                if (File.Exists(SidecarPath(slotID))) File.Delete(SidecarPath(slotID));
            }
            catch (Exception e)
            {
                Plugin.BepinLogger.LogError($"SaveSync delete hook failed: {e}");
            }
        }
    }

    private static void OnTimelineReset()
    {
        TimelineActive = true;
        CurrentSlot = 0;
        AppliedIndex = 0;
        SeedMismatch = false;
        RunSeed = ArchipelagoClient.Authenticated ? ArchipelagoClient.ServerData.Seed : null;
        RunSlotName = ArchipelagoClient.Authenticated ? ArchipelagoClient.ServerData.SlotName : null;
        Plugin.BepinLogger.LogInfo("SaveSync: new game; received items will apply into the fresh globals.");
    }

    private static void OnGameplayLoaded(int slot)
    {
        TimelineActive = true;
        CurrentSlot = slot;
        SeedMismatch = false;
        Sidecar sc = ReadSidecar(slot);
        if (sc == null)
        {
            // pre-mod or vanilla save: adopt it; everything received so far
            // applies once and the next save records that
            AppliedIndex = 0;
            RunSeed = null;
            RunSlotName = null;
            Plugin.BepinLogger.LogWarning($"SaveSync: slot {slot} has no AP sidecar; adopting it into this multiworld.");
        }
        else
        {
            AppliedIndex = sc.appliedIndex;
            RunSeed = sc.seed;
            RunSlotName = sc.slotName;
            MergeCheckedLocations(sc.checkedLocations);
        }
        ReevaluateSeedBinding();
        Plugin.BepinLogger.LogInfo($"SaveSync: loaded slot {slot}, applied-item index {AppliedIndex}.");
    }

    private static void OnGameplaySaved(int slot)
    {
        if (SeedMismatch) return; // never overwrite a foreign save's sidecar
        TimelineActive = true;
        CurrentSlot = slot;
        if (string.IsNullOrEmpty(RunSeed) && ArchipelagoClient.Authenticated)
        {
            RunSeed = ArchipelagoClient.ServerData.Seed;
            RunSlotName = ArchipelagoClient.ServerData.SlotName;
        }
        WriteSidecar(slot);
    }

    /// <summary>
    /// compare the run's recorded multiworld identity against the connected
    /// session; called after a save load and after every successful connect.
    /// On mismatch all AP behavior is disabled so a foreign save can neither
    /// receive items nor send checks into the wrong multiworld.
    /// </summary>
    public static void ReevaluateSeedBinding()
    {
        if (!ArchipelagoClient.Authenticated || !TimelineActive) return;
        var data = ArchipelagoClient.ServerData;
        if (string.IsNullOrEmpty(RunSeed))
        {
            RunSeed = data.Seed;
            RunSlotName = data.SlotName;
            return;
        }
        bool mismatch = RunSeed != data.Seed ||
                        (!string.IsNullOrEmpty(RunSlotName) && RunSlotName != data.SlotName);
        if (mismatch && !SeedMismatch)
        {
            SeedMismatch = true;
            string msg = $"This save belongs to a different multiworld (slot '{RunSlotName}'). " +
                         "AP items and checks are DISABLED for it - load the matching save or start a new game.";
            Plugin.BepinLogger.LogWarning(msg);
            ArchipelagoConsole.LogMessage(msg);
        }
        else if (!mismatch)
        {
            SeedMismatch = false;
        }
    }

    private static void MergeCheckedLocations(List<long> ids)
    {
        if (ids == null || ids.Count == 0) return;
        var known = ArchipelagoClient.ServerData.CheckedLocations;
        List<long> fresh = new();
        foreach (long id in ids)
        {
            if (!known.Contains(id))
            {
                known.Add(id);
                fresh.Add(id);
            }
        }
        // checks recorded in the sidecar but unknown to this process were
        // made in an earlier session; make sure the server has them
        if (fresh.Count > 0) Plugin.ArchipelagoClient?.ResendChecks(fresh);
    }

    private static Sidecar ReadSidecar(int slot)
    {
        try
        {
            string path = SidecarPath(slot);
            if (!File.Exists(path)) return null;
            return JsonConvert.DeserializeObject<Sidecar>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError($"SaveSync: failed reading sidecar for slot {slot}: {e}");
            return null;
        }
    }

    private static void WriteSidecar(int slot)
    {
        try
        {
            var sc = new Sidecar
            {
                seed = RunSeed ?? "",
                slotName = RunSlotName ?? "",
                appliedIndex = AppliedIndex,
                checkedLocations = new List<long>(ArchipelagoClient.ServerData.CheckedLocations),
            };
            File.WriteAllText(SidecarPath(slot), JsonConvert.SerializeObject(sc, Formatting.Indented));
            Plugin.BepinLogger.LogInfo($"SaveSync: slot {slot} saved at applied-item index {AppliedIndex}.");
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError($"SaveSync: failed writing sidecar for slot {slot}: {e}");
        }
    }
}
