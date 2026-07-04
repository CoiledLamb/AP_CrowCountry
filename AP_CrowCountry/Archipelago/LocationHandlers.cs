using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace APCrowCountry.Archipelago;

public class Location {
    public long apid;
    public bool pickup;
    public string region;
    public string id;

    public Location(long apid, bool pickup, string region, string id) {
        this.apid = apid;
        this.pickup = pickup;
        this.region = region;
        this.id = id;
    }
}
public class LocationHandlers
{
    [HarmonyPatch(typeof(Fsm), "SwitchState")]
    public static class Fsm_SwitchState
    {
        public static void Prefix(Fsm __instance, ref FsmState toState)
        {
            // Not connected (or the loaded save belongs to another multiworld)
            // -> leave the game fully vanilla. Also never let an exception
            // escape into Fsm.SwitchState: a throw here kills the state
            // transition and soft-locks the player.
            if (!ArchipelagoClient.Authenticated || SaveSync.SeedMismatch ||
                Plugin.ArchipelagoClient?.Locations == null)
                return;
            try
            {
                HandleSwitchState(__instance, ref toState);
            }
            catch (System.Exception e)
            {
                Plugin.BepinLogger.LogError($"Location hook failed on '{__instance.GameObjectName}' -> '{toState.Name}': {e}");
            }
        }

        private static void HandleSwitchState(Fsm __instance, ref FsmState toState)
        {
            // Key-item interactables (grant pattern "set item INT" -> toast;
            // anatomy in datamine/KEYITEMS.md). Identified by a "keyitem:"
            // marker in locations.json matched against ancestor object names.
            if (__instance.GameObjectName == "FLOW CODE" || __instance.GameObjectName == "flowcode")
            {
                Location keyLoc = ResolveKeyInteractable(__instance);
                if (keyLoc != null)
                {
                    HandleKeyInteractable(__instance, ref toState, keyLoc);
                    return;
                }
            }

            if (__instance.GameObjectName == "FLOW CODE" && __instance.GameObject.transform.parent.parent.parent.parent.name != "Pickup Trap" && __instance.GameObject.transform.parent.parent.parent.name.IndexOf("vending machine") == -1)
            {
                //UnityEngine.Debug.Log(toState.Name);

                // The pickup's display sequence ("set ItemAction" -> raised
                // model branch -> toast) reads local FSM vars; overwrite them
                // with the scouted AP item so vanilla presents the truth.
                if (toState.Name.IndexOf("set ItemAction") != -1) {
                    OverridePickupDisplay(__instance);
                }

                // Vanilla dupe protection: retype before the fullness
                // dispatcher so its "full? N" branch evaluates the true AP
                // item -- capped items say "I can't carry any more" and stay
                // on the ground, uncollected, exactly like vanilla.
                if (toState.Name.IndexOf("which type? 2") != -1) {
                    OverridePickupDisplay(__instance);
                }

                // ...but our inventory caps must never hold a non-consumable
                // or another player's item hostage: skip to the take prompt.
                if (toState.Name.IndexOf("full?") != -1 && !ScoutedOwnConsumable(__instance)) {
                    foreach (var t in toState.Transitions) {
                        if (t.EventName == "not full") {
                            toState = t.ToFsmState;
                            break;
                        }
                    }
                }

                // Pickups: suppress the vanilla grant and send the check --
                // but ONLY for mapped locations. Unmapped pickups (areas not
                // yet in locations.json) must stay fully vanilla, or their
                // items are silently destroyed.
                if (toState.Name.IndexOf("add item") != -1) {
                    Location loc = ResolveArrayPickup(__instance);
                    if (loc != null) {
                        SendAndPopup(loc, "pickup");
                        toState = toState.Transitions[0].ToFsmState;
                    }
                }
            }
            if (__instance.GameObjectName == "flowcode" && __instance.GameObject.transform.parent.parent.parent.name.IndexOf("Trash can") != -1)
            {
                // Trashcan (unmapped ones stay vanilla)
                if (toState.Name.IndexOf("take") != -1) {
                    Location loc = ResolveByObjectName(__instance);
                    if (loc != null) SendAndPopup(loc, "trashcan");
                }
                if (toState.Name.IndexOf("add") != -1 && ResolveByObjectName(__instance) != null) {
                    toState = toState.Transitions[0].ToFsmState;
                }
            }
            if (__instance.GameObjectName == "FLOW CODE" && __instance.GameObject.transform.parent.parent.parent.name.IndexOf("vending machine") != -1)
            {
                // Vending Machine (unmapped ones stay vanilla)
                if (toState.Name.IndexOf("given heals already?") != -1 && ResolveByObjectName(__instance) != null) {
                    toState = toState.Transitions[0].ToFsmState;
                }
                if (toState.Name.IndexOf("add") != -1) {
                    Location loc = ResolveByObjectName(__instance);
                    if (loc != null) {
                        SendAndPopup(loc, "vending machine");
                        toState = toState.Transitions[0].ToFsmState;
                    }
                }
            }
            if (__instance.GameObjectName == "Select a Pickup") {
                //UnityEngine.Debug.Log(toState.Name);

                // Wooden Box
                if (toState.Name.IndexOf("random choice") != -1) {
                    toState = toState.Transitions[2].ToFsmState;
                }
            }
        }
    }

    // Old Pocket Light and Laser Sight code
    /*[HarmonyPatch(typeof(SetBoolValue), "OnEnter")]
    public static class SetBoolValue_OnEnter
    {
        public static void Prefix(SetBoolValue __instance, out bool __state)
        {
            __state = false;
            if (__instance.Owner.name == "FLOW CODE")
            {
                if ((__instance.boolVariable.Name == "light found" || __instance.boolVariable.Name == "Hangun Laser") && __instance.Owner.scene.name == "Toilet") {
                    UnityEngine.Debug.Log("Light found");
                    SendSpecialLocation("Entrance", "PocketLight");
                    __state = true;
                }
                if ((__instance.boolVariable.Name == "light found" || __instance.boolVariable.Name == "Hangun Laser") && __instance.Owner.scene.name == "Station Square") {
                    UnityEngine.Debug.Log("Laser sight found");
                    //SendSpecialLocation("Entrance", "LaserSight");
                    //__state = true;
                }
            }
        }
        public static void Postfix(SetBoolValue __instance, bool __state)
        {
            if (__state)
            {
                __instance.Fsm.Event("light found");
                __instance.boolValue = false;
                __instance.boolVariable.Value = false;
            }
        }
    }*/

    /// <summary>
    /// key-item locations use id "keyitem:&lt;ancestor object name&gt;";
    /// match by walking the FSM object's transform ancestors
    /// </summary>
    private static Location ResolveKeyInteractable(Fsm __instance) {
        string sceneName = __instance.GameObject.scene.name;
        try {
            foreach (Location location in Plugin.ArchipelagoClient.Locations.Values) {
                if (location.region != sceneName || !location.id.StartsWith("keyitem:")) continue;
                string marker = location.id.Substring("keyitem:".Length);
                for (var t = __instance.GameObject.transform; t != null; t = t.parent)
                    if (t.name == marker) return location;
            }
        } catch { }
        return null;
    }

    /// <summary>
    /// randomize a key-item interactable:
    /// - "have item?" checks vanilla ownership at examine time, but under
    ///   randomization ownership says nothing about whether the CHECK was
    ///   made (we may already hold the key from the multiworld). Force the
    ///   branch by checked-state: unchecked -> not-owned flow (take prompt),
    ///   checked -> owned flow ("thanks" dialog, key model stays hidden).
    /// - "set item INT" is the vanilla grant: suppress it, send the check,
    ///   show our own truthful toast, AND skip the vanilla toast-builder
    ///   state ("set ItemAction strings") it flows into -- redirecting the
    ///   FSM straight into that state would run its actions (overwriting our
    ///   popup with the vanilla item name + hardcoded icon) because this
    ///   prefix does not re-fire for a redirect it just made.
    /// </summary>
    private static void HandleKeyInteractable(Fsm __instance, ref FsmState toState, Location keyLoc) {
        bool isChecked = ArchipelagoClient.ServerData.CheckedLocations.Contains(keyLoc.apid);

        if (toState.Name.IndexOf("have item?") != -1) {
            string ev = isChecked ? "y" : "n";
            foreach (var t in toState.Transitions) {
                if (t.EventName == ev) {
                    toState = t.ToFsmState;
                    break;
                }
            }
        }
        else if (toState.Name.IndexOf("set item INT") != -1) {
            SendAndPopup(keyLoc, "key interactable");
            var next = toState.Transitions[0].ToFsmState;
            // hop over the vanilla "set ItemAction strings" toast builder so
            // it can't clobber the popup we just set; the following states
            // ("set UI sfx" / "set UI bools N") still fire the toast, reading
            // our globals
            if (next.Name.IndexOf("ItemAction") != -1 && next.Transitions.Length > 0)
                next = next.Transitions[0].ToFsmState;
            toState = next;
        }
    }

    /// <summary>resolve trash cans / vending machines by their object name</summary>
    private static Location ResolveByObjectName(Fsm __instance) {
        string sceneName = __instance.GameObject.scene.name;
        try {
            string id = __instance.GameObject.transform.parent.parent.parent.name;
            foreach (Location location in Plugin.ArchipelagoClient.Locations.Values)
                if (location.region == sceneName && location.id == id) return location;
        } catch { }
        return null;
    }

    // checked-state must be captured BEFORE the send records the location
    private static void SendAndPopup(Location location, string kind) {
        bool alreadyChecked = ArchipelagoClient.ServerData.CheckedLocations.Contains(location.apid);
        UnityEngine.Debug.Log($"Sending location {location.apid} from {kind} (already checked: {alreadyChecked})");
        Plugin.ArchipelagoClient.SendLocationCheck(location.apid);
        Plugin.ArchipelagoClient.ShowCheckPopup(location.apid, alreadyChecked);
    }

    // vanilla ArrayPickup "item type" branch values, from the add-item grant states
    private static readonly Dictionary<string, int> consumableTypeInts = new() {
        { "Small Med Kit", 1 },
        { "Large Med Kit", 2 },
        { "Antidote", 3 },
        { "Handgun Ammo", 4 },
        { "Shotgun Ammo", 5 },
        { "Magnum Ammo", 7 },
        { "Grenade", 8 },
    };

    /// <summary>
    /// resolve an ArrayPickup FSM to its mapped location. The number-child id
    /// is NOT unique within a scene (crate/bottle inner pickups share "0"),
    /// so container detection is exclusive: a pickup inside a Wooden Crate
    /// only ever matches a "Wooden Crate N" id, a Glass Bottle matches its
    /// number, an unknown container matches nothing (stays vanilla).
    /// Hierarchy: [container]/model/pickup/ArrayPickup/{number-child, code/
    /// examine set/examine set parent/FLOW CODE}; bare pickups sit under a
    /// "pickups" group instead of a container's "pickup" node.
    /// </summary>
    private static Location ResolveArrayPickup(Fsm __instance) {
        string sceneName = __instance.GameObject.scene.name;
        string wantedId;
        try {
            var ap = __instance.GameObject.transform.parent.parent.parent.parent; // ArrayPickup
            if (ap == null || ap.childCount == 0) return null;
            string num = ap.GetChild(0).name;
            var group = ap.parent;                                   // "pickup" when containerized
            var container = group != null && group.parent != null ? group.parent.parent : null;
            bool contained = group != null && group.name == "pickup" && container != null;
            if (contained && container.name.IndexOf("Wooden Crate") != -1) {
                if (container.childCount == 0) return null;
                wantedId = "Wooden Crate " + container.GetChild(0).name;
            } else if (!contained || container.name.IndexOf("Glass Bottle") != -1) {
                wantedId = num;
            } else {
                return null; // unknown container type
            }
        } catch { return null; }
        foreach (Location location in Plugin.ArchipelagoClient.Locations.Values)
            if (location.region == sceneName && location.id == wantedId) return location;
        return null;
    }

    /// <summary>
    /// rewrite the pickup FSM's local display vars ("item name", "display
    /// number", "item type", "item description") so vanilla's toast, raised
    /// model, examine text and fullness check all operate on the scouted AP
    /// item. "item type" is only retyped for consumables that have a native
    /// model; the grant is still suppressed at "add item" regardless of type.
    /// </summary>
    public static void OverridePickupDisplay(Fsm __instance) {
        Location loc = ResolveArrayPickup(__instance);
        if (loc == null) return;
        if (!Plugin.ArchipelagoClient.ScoutedLocations.TryGetValue(loc.apid, out var info)) return;

        bool claimed = ArchipelagoClient.ServerData.CheckedLocations.Contains(loc.apid);
        string display = info.ForMe ? info.ItemName : $"{info.ItemName} → {info.PlayerName}";
        if (claimed) display += " (already claimed)";
        int icon = info.ForMe && ItemFinder.itemIcons.TryGetValue(info.ItemName, out int mapped)
            ? mapped : ItemFinder.GenericIcon;

        var nameVar = __instance.Variables.GetFsmString("item name");
        if (nameVar != null) nameVar.Value = display;
        var displayVar = __instance.Variables.GetFsmInt("display number");
        if (displayVar != null) displayVar.Value = icon;
        var descVar = __instance.Variables.GetFsmString("item description");
        if (descVar != null)
            descVar.Value = info.ForMe
                ? $"It's a {info.ItemName}."
                : $"It's {info.PlayerName}'s {info.ItemName}.";
        if (info.ForMe && consumableTypeInts.TryGetValue(info.ItemName, out int type)) {
            var typeVar = __instance.Variables.GetFsmInt("item type");
            if (typeVar != null) typeVar.Value = type;
        }
    }

    /// <summary>
    /// true when the scouted item at this pickup is one of our own capped
    /// consumables -- the only case where vanilla's fullness check should
    /// apply. Unmapped or unscouted pickups stay fully vanilla (true).
    /// Already-claimed pickups (respawned by a save reload) grant nothing,
    /// so fullness must not block clearing them (false).
    /// </summary>
    private static bool ScoutedOwnConsumable(Fsm __instance) {
        Location loc = ResolveArrayPickup(__instance);
        if (loc == null) return true;
        if (!Plugin.ArchipelagoClient.ScoutedLocations.TryGetValue(loc.apid, out var info)) return true;
        if (ArchipelagoClient.ServerData.CheckedLocations.Contains(loc.apid)) return false;
        return info.ForMe && consumableTypeInts.ContainsKey(info.ItemName);
    }

    public static void SendSpecialLocation(string sceneName, string id) {
        foreach (Location location in Plugin.ArchipelagoClient.Locations.Values) {
            if (location.region == sceneName && location.id == id) {
                Plugin.ArchipelagoClient.SendLocationCheck(location.apid);
                break;
            }
        }
    }
}