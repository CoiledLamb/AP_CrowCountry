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
    };

    public static Dictionary<string, FsmBool> controlledBools = new();

    public static List<string> validBools = new List<string>() {
        "light found",
        "Handgun Laser"
    };

    // Equipment flags received from the multiworld. The game's "New Game"
    // flow resets all PlayMaker globals and loading a save overwrites them,
    // so a one-shot write at receive time gets clobbered; Update() re-asserts
    // these every frame instead.
    public static readonly HashSet<string> receivedBools = new();

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
    }

    public static void Update() {
        // vanilla light pickup only gets suppressed while actually randomized
        if (!ArchipelagoClient.Authenticated) return;

        foreach (string name in receivedBools) {
            if (controlledBools.TryGetValue(name, out var fsmBool) && !fsmBool.Value)
                fsmBool.Value = true;
        }

        var anyPocketLight = GameObject.Find("pocket light setup");
        if (anyPocketLight != null) {
            anyPocketLight.SetActive(false);
        }
    }
}