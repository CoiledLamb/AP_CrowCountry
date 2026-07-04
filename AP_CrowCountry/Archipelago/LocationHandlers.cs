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
            // Not connected -> no location table; leave the game fully vanilla.
            // Also never let an exception escape into Fsm.SwitchState: a throw
            // here kills the state transition and soft-locks the player.
            if (!ArchipelagoClient.Authenticated || Plugin.ArchipelagoClient?.Locations == null)
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
            if (__instance.GameObjectName == "FLOW CODE" && __instance.GameObject.transform.parent.parent.parent.parent.name != "Pickup Trap" && __instance.GameObject.transform.parent.parent.parent.name.IndexOf("vending machine") == -1)
            {
                //UnityEngine.Debug.Log(toState.Name);

                // The pickup's display sequence ("set ItemAction" -> raised
                // model branch -> toast) reads local FSM vars; overwrite them
                // with the scouted AP item so vanilla presents the truth.
                if (toState.Name.IndexOf("set ItemAction") != -1) {
                    OverridePickupDisplay(__instance);
                }

                // Pickups
                if (toState.Name.IndexOf("add item") != -1) {
                    // Wooden Crate
                    if (__instance.GameObject.transform.parent.parent.parent.parent.parent.parent.parent.name.IndexOf("Wooden Crate") != -1) {
                        //UnityEngine.Debug.Log("Adding item");
                        SendLocationFromCrate(__instance, toState);
                        toState = toState.Transitions[0].ToFsmState;
                        //UnityEngine.Debug.Log(toState.Name);
                    } else {
                        //UnityEngine.Debug.Log("Adding item");
                        SendLocationFromPickup(__instance, toState);
                        toState = toState.Transitions[0].ToFsmState;
                        //UnityEngine.Debug.Log(toState.Name);
                    }
                }
            }
            if (__instance.GameObjectName == "flowcode" && __instance.GameObject.transform.parent.parent.parent.name.IndexOf("Trash can") != -1)
            {
                UnityEngine.Debug.Log(toState.Name);

                // Trashcan
                if (toState.Name.IndexOf("take") != -1) {
                    //UnityEngine.Debug.Log("Adding item");
                    SendLocationFromObject(__instance, toState);
                    //UnityEngine.Debug.Log(toState.Name);
                }
                if (toState.Name.IndexOf("add") != -1) {
                    //UnityEngine.Debug.Log("Adding item");
                    toState = toState.Transitions[0].ToFsmState;
                    //UnityEngine.Debug.Log(toState.Name);
                }
            }
            if (__instance.GameObjectName == "FLOW CODE" && __instance.GameObject.transform.parent.parent.parent.name.IndexOf("vending machine") != -1)
            {
                //UnityEngine.Debug.Log(toState.Name);

                // Vending Machine
                if (toState.Name.IndexOf("given heals already?") != -1) {
                    //UnityEngine.Debug.Log("Adding item");
                    toState = toState.Transitions[0].ToFsmState;
                    //UnityEngine.Debug.Log(toState.Name);
                }
                if (toState.Name.IndexOf("add") != -1) {
                    //UnityEngine.Debug.Log("Adding item");
                    SendLocationFromObject(__instance, toState);
                    toState = toState.Transitions[0].ToFsmState;
                    //UnityEngine.Debug.Log(toState.Name);
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

    public static void SendLocationFromPickup(Fsm __instance, FsmState toState) {
        string sceneName = __instance.GameObject.scene.name;
        UnityEngine.Debug.Log(sceneName);
        string id = __instance.GameObject.transform.parent.parent.parent.parent.GetChild(0).name;
        UnityEngine.Debug.Log(id);
        foreach (Location location in Plugin.ArchipelagoClient.Locations.Values) {
            if (location.region == sceneName && location.id == id) {
                UnityEngine.Debug.Log("Sending location " + location.apid + " from pickup");
                Plugin.ArchipelagoClient.SendLocationCheck(location.apid);
                Plugin.ArchipelagoClient.ShowCheckPopup(location.apid);
                break;
            }
        }
    }

    public static void SendLocationFromCrate(Fsm __instance, FsmState toState) {
        string sceneName = __instance.GameObject.scene.name;
        UnityEngine.Debug.Log(sceneName);
        string id = __instance.GameObject.transform.parent.parent.parent.parent.parent.parent.parent.GetChild(0).name;
        UnityEngine.Debug.Log(id);
        foreach (Location location in Plugin.ArchipelagoClient.Locations.Values) {
            if (location.region == sceneName && location.id == "Wooden Crate "+ id) {
                UnityEngine.Debug.Log("Sending location " + location.apid + " from pickup");
                Plugin.ArchipelagoClient.SendLocationCheck(location.apid);
                Plugin.ArchipelagoClient.ShowCheckPopup(location.apid);
                break;
            }
        }
    }

    public static void SendLocationFromObject(Fsm __instance, FsmState toState) {
        string sceneName = __instance.GameObject.scene.name;
        UnityEngine.Debug.Log(sceneName);
        string id = __instance.GameObject.transform.parent.parent.parent.name;
        UnityEngine.Debug.Log(id);
        foreach (Location location in Plugin.ArchipelagoClient.Locations.Values) {
            if (location.region == sceneName && location.id == id) {
                UnityEngine.Debug.Log("Sending location " + location.apid + " from trashcan");
                Plugin.ArchipelagoClient.SendLocationCheck(location.apid);
                Plugin.ArchipelagoClient.ShowCheckPopup(location.apid);
                break;
            }
        }
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

    private static Location ResolveArrayPickup(Fsm __instance) {
        string sceneName = __instance.GameObject.scene.name;
        try {
            string id = __instance.GameObject.transform.parent.parent.parent.parent.GetChild(0).name;
            foreach (Location location in Plugin.ArchipelagoClient.Locations.Values)
                if (location.region == sceneName && location.id == id) return location;
        } catch { }
        try {
            var crate = __instance.GameObject.transform.parent.parent.parent.parent.parent.parent.parent;
            if (crate.name.IndexOf("Wooden Crate") != -1) {
                string id = crate.GetChild(0).name;
                foreach (Location location in Plugin.ArchipelagoClient.Locations.Values)
                    if (location.region == sceneName && location.id == "Wooden Crate " + id) return location;
            }
        } catch { }
        return null;
    }

    /// <summary>
    /// rewrite the pickup FSM's local display vars ("item name", "display
    /// number", "item type") before its "set ItemAction" state runs, so the
    /// vanilla toast + raised model show the scouted AP item. "item type" is
    /// only retyped for consumables that have a native model; the grant is
    /// still suppressed at "add item" regardless of type.
    /// </summary>
    public static void OverridePickupDisplay(Fsm __instance) {
        Location loc = ResolveArrayPickup(__instance);
        if (loc == null) return;
        if (!Plugin.ArchipelagoClient.ScoutedLocations.TryGetValue(loc.apid, out var info)) return;

        string display = info.ForMe ? info.ItemName : $"{info.ItemName} → {info.PlayerName}";
        int icon = info.ForMe && ItemFinder.itemIcons.TryGetValue(info.ItemName, out int mapped)
            ? mapped : ItemFinder.GenericIcon;

        var nameVar = __instance.Variables.GetFsmString("item name");
        if (nameVar != null) nameVar.Value = display;
        var displayVar = __instance.Variables.GetFsmInt("display number");
        if (displayVar != null) displayVar.Value = icon;
        if (info.ForMe && consumableTypeInts.TryGetValue(info.ItemName, out int type)) {
            var typeVar = __instance.Variables.GetFsmInt("item type");
            if (typeVar != null) typeVar.Value = type;
        }
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