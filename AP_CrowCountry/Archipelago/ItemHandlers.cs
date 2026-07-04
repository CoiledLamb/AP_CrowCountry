using UnityEngine;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HarmonyLib;
using APCrowCountry.Utils;

namespace APCrowCountry.Archipelago;

public class ItemFinder {

    public static Dictionary<string, FsmInt> itemTypes = new();

    public static List<string> validItems = new List<string>() {
        "Antidotes",
        "Antidotes MAX",
        "Grenades",
        "Grenades MAX",
        "Heals Small",
        "Heals Small MAX",
        "Heals Large",
        "Heals Large MAX",
        "Ammo in Box",
        "Ammo in Box MAX",
        "Shotgun in Box",
        "Shotgun in Box MAX",
        "Magnum in Box",
        "Magnum in Box MAX",
        "Flame in Box",
        "Flame in Box MAX",
        "ItemActionDisplay",
    };

    public static Dictionary<string, FsmString> controlledStrings = new();

    public static List<string> validStrings = new List<string>() {
        "Glb ItemAction Action",
        "Glb ItemAction Name",
    };

    public static Dictionary<string, FsmBool> controlledBools = new();

    public static List<string> validBools = new List<string>() {
        "light found",
        "Handgun Laser",
        "UI ItemAction",
    };

    // vanilla ItemActionDisplay icon indexes, mined from the pickup FSMs
    public static readonly Dictionary<string, int> itemIcons = new() {
        { "Handgun Ammo", 14 },
        { "Small Med Kit", 15 },
        { "Large Med Kit", 16 },
        { "Antidote", 17 },
        { "Grenade", 18 },
        { "Shotgun Ammo", 20 },
        { "Magnum Ammo", 14 },
        { "Pocket Light", 29 },
        { "Handgun Laser Sight", 30 },
    };
    public const int GenericIcon = 39; // "Paper" - neutral, used for other players' items


    private static bool initializedItems = false;
    private static bool initializedBools = false;

    public static void InitializeItemTypes() {
        ArchipelagoConsole.LogMessage("Initializing item types");
        if (initializedItems) return;
        initializedItems = true;
        PlayMakerGlobals allVariables = PlayMakerGlobals.Instance;
        FsmInt[] intVariables = allVariables.Variables.IntVariables;
        for (int i = 0; i < intVariables.Length; i++) {
            ArchipelagoConsole.LogMessage(intVariables[i].Name);
            if (validItems.Contains(intVariables[i].Name)) {
                itemTypes.Add(intVariables[i].Name, intVariables[i]);
            }
        }
    }

    public static void InitializeControlledBools() {
        ArchipelagoConsole.LogMessage("Initializing controlled bools");
        if (initializedBools) return;
        initializedBools = true;
        PlayMakerGlobals allVariables = PlayMakerGlobals.Instance;
        FsmBool[] boolVariables = allVariables.Variables.BoolVariables;
        for (int i = 0; i < boolVariables.Length; i++) {
            ArchipelagoConsole.LogMessage(boolVariables[i].Name);
            if (validBools.Contains(boolVariables[i].Name)) {
                controlledBools.Add(boolVariables[i].Name, boolVariables[i]);
            }
        }
        FsmString[] stringVariables = allVariables.Variables.StringVariables;
        for (int i = 0; i < stringVariables.Length; i++) {
            if (validStrings.Contains(stringVariables[i].Name)) {
                controlledStrings.Add(stringVariables[i].Name, stringVariables[i]);
            }
        }
    }

    /// <summary>
    /// drive the game's native "Obtained: X" toast with arbitrary contents.
    /// Same mechanism the vanilla pickup FSMs use.
    /// </summary>
    public static void ShowItemPopup(string action, string itemName, int icon) {
        if (!controlledStrings.ContainsKey("Glb ItemAction Name") ||
            !controlledBools.ContainsKey("UI ItemAction") ||
            !itemTypes.ContainsKey("ItemActionDisplay"))
            return;
        controlledStrings["Glb ItemAction Action"].Value = action;
        controlledStrings["Glb ItemAction Name"].Value = itemName;
        itemTypes["ItemActionDisplay"].Value = icon;
        controlledBools["UI ItemAction"].Value = true;
    }

    /// <summary>increment a consumable counter global, e.g. "Heals Small"</summary>
    public static void AddToCounter(string name, int delta) {
        if (itemTypes.TryGetValue(name, out var fsmInt)) fsmInt.Value += delta;
    }

    /// <summary>set an equipment flag global, e.g. "light found"</summary>
    public static void SetGlobalBool(string name) {
        if (controlledBools.TryGetValue(name, out var fsmBool)) fsmBool.Value = true;
    }

    public static void Update() {
        // wrong save for this multiworld -> stay fully vanilla
        if (!ArchipelagoClient.Authenticated || SaveSync.SeedMismatch) return;

        // vanilla light pickup only gets suppressed while actually randomized
        var anyPocketLight = GameObject.Find("pocket light setup");
        if (anyPocketLight != null) {
            anyPocketLight.SetActive(false);
        }
    }
}