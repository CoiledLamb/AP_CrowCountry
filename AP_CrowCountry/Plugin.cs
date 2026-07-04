using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using APCrowCountry.Archipelago;
using APCrowCountry.Utils;
using UnityEngine;
using HarmonyLib;

namespace APCrowCountry;

[BepInPlugin(PluginGUID, PluginName, PluginVersion)]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGUID = "chandler.apcrowcountry";
    public const string PluginName = "AP Crow Country";
    public const string PluginVersion = "0.0.1";

    public const string ModDisplayInfo = $"{PluginName} v{PluginVersion}";
    private const string APDisplayInfo = $"Archipelago v{ArchipelagoClient.APVersion}";
    public static ManualLogSource BepinLogger;
    public static ArchipelagoClient ArchipelagoClient;

    public static ConfigEntry<string> CfgHost;
    public static ConfigEntry<string> CfgSlot;
    public static ConfigEntry<string> CfgPassword;
    public static ConfigEntry<bool> CfgAutoConnect;
    private float autoConnectAt = 10f;

    private void Awake()
    {
        // Plugin startup logic
        BepinLogger = Logger;
        ArchipelagoClient = new ArchipelagoClient();
        ArchipelagoConsole.Awake();

        CfgHost = Config.Bind("Connection", "Host", "localhost:38281", "Archipelago server address (host:port)");
        CfgSlot = Config.Bind("Connection", "SlotName", "", "Slot/player name in the multiworld");
        CfgPassword = Config.Bind("Connection", "Password", "", "Server password (leave empty for none)");
        CfgAutoConnect = Config.Bind("Connection", "AutoConnect", false, "Connect automatically shortly after launch");

        ArchipelagoClient.ServerData.Uri = CfgHost.Value;
        if (!CfgSlot.Value.IsNullOrWhiteSpace())
            ArchipelagoClient.ServerData.SlotName = CfgSlot.Value;
        ArchipelagoClient.ServerData.Password = CfgPassword.Value;

        ArchipelagoConsole.LogMessage($"{ModDisplayInfo} loaded!");

        var harmony = new Harmony("apcrowcountry.harmony");
        harmony.PatchAll();
    }

    private bool showConnectUI;

    private void OnGUI()
    {
        // minimal HUD: one status line; everything else lives behind F1
        // (connection is normally config-driven - see chandler.apcrowcountry.cfg)
        string status = SaveSync.SeedMismatch
            ? "AP: SAVE IS FROM A DIFFERENT MULTIWORLD - randomizer disabled for it"
            : ArchipelagoClient.Authenticated
                ? $"AP: {ArchipelagoClient.ServerData.SlotName} connected"
                : "AP: disconnected [F1]";
        GUI.Label(new Rect(16, 16, 300, 20), status);

        if (!showConnectUI)
            return;

        GUI.Label(new Rect(16, 36, 300, 20), $"{ModDisplayInfo} / {APDisplayInfo}");
        ArchipelagoConsole.OnGUI();

        if (!ArchipelagoClient.Authenticated)
        {
            GUI.Label(new Rect(16, 70, 150, 20), "Host: ");
            GUI.Label(new Rect(16, 90, 150, 20), "Player Name: ");
            GUI.Label(new Rect(16, 110, 150, 20), "Password: ");

            ArchipelagoClient.ServerData.Uri = GUI.TextField(new Rect(150, 70, 150, 20),
                ArchipelagoClient.ServerData.Uri);
            ArchipelagoClient.ServerData.SlotName = GUI.TextField(new Rect(150, 90, 150, 20),
                ArchipelagoClient.ServerData.SlotName);
            ArchipelagoClient.ServerData.Password = GUI.TextField(new Rect(150, 110, 150, 20),
                ArchipelagoClient.ServerData.Password);

            // requires that the player at least puts *something* in the slot name
            if (GUI.Button(new Rect(16, 130, 100, 20), "Connect") &&
                !ArchipelagoClient.ServerData.SlotName.IsNullOrWhiteSpace())
            {
                ArchipelagoClient.Connect();
            }
        }
    }

    private void Update() {
        if (Input.GetKeyDown(KeyCode.F1))
            showConnectUI = !showConnectUI;
        // deferred so PlayMaker globals exist before the first connect attempt;
        // retries every 30s until connected
        if (CfgAutoConnect.Value && !ArchipelagoClient.Authenticated && Time.time > autoConnectAt)
        {
            autoConnectAt = Time.time + 30f;
            ArchipelagoConsole.LogMessage($"Auto-connecting to {ArchipelagoClient.ServerData.Uri} as {ArchipelagoClient.ServerData.SlotName}...");
            ArchipelagoClient.Connect();
        }
        ItemFinder.Update();
        ArchipelagoClient.ApplyPendingItems();
    }
}