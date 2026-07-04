using System;
using System.Linq;
using System.Threading;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.Models;
using Archipelago.MultiClient.Net.Packets;
using APCrowCountry.Utils;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace APCrowCountry.Archipelago;

public class ArchipelagoClient
{
    public const string APVersion = "0.6.8";
    private const string Game = "Crow Country";

    public static bool Authenticated;
    private bool attemptingConnection;

    // Goal: defeat Edward Crowley. His death is the only way the Ending
    // Sequence scene ever loads (the boss FSM's "load ending" state, in both
    // combat and Exploration Mode), so scene arrival = goal complete.
    private const string EndingSceneName = "Ending Sequence";
    private bool goalSent;

    public static ArchipelagoData ServerData = new();
    private DeathLinkHandler DeathLinkHandler;
    private ArchipelagoSession session;
    public Dictionary<long, Location> Locations;

    public class ScoutedInfo
    {
        public string ItemName;
        public string PlayerName;
        public bool ForMe;
    }
    public Dictionary<long, ScoutedInfo> ScoutedLocations = new();

    /// <summary>
    /// call to connect to an Archipelago session. Connection info should already be set up on ServerData
    /// </summary>
    /// <returns></returns>
    public void Connect()
    {
        if (Authenticated || attemptingConnection) return;

        try
        {
            session = ArchipelagoSessionFactory.CreateSession(ServerData.Uri);
            SetupSession();
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError(e);
        }

        TryConnect();
    }

    /// <summary>
    /// add handlers for Archipelago events
    /// </summary>
    private void SetupSession()
    {
        session.MessageLog.OnMessageReceived += message => ArchipelagoConsole.LogMessage(message.ToString());
        session.Items.ItemReceived += OnItemReceived;
        session.Socket.ErrorReceived += OnSessionErrorReceived;
        session.Socket.SocketClosed += OnSessionSocketClosed;
    }

    /// <summary>
    /// attempt to connect to the server with our connection info
    /// </summary>
    private void TryConnect()
    {
        try
        {
            ItemFinder.InitializeItemTypes();
            ItemFinder.InitializeControlledBools();
            // it's safe to thread this function call but unity notoriously hates threading so do not use excessively
            ThreadPool.QueueUserWorkItem(
                _ => HandleConnectResult(
                    session.TryConnectAndLogin(
                        Game,
                        ServerData.SlotName,
                        ItemsHandlingFlags.AllItems, // TODO make sure to change this line
                        new Version(APVersion),
                        password: ServerData.Password,
                        requestSlotData: false // ServerData.NeedSlotData
                    )));
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError(e);
            HandleConnectResult(new LoginFailure(e.ToString()));
            attemptingConnection = false;
        }
    }

    /// <summary>
    /// handle the connection result and do things
    /// </summary>
    /// <param name="result"></param>
    private void HandleConnectResult(LoginResult result)
    {
        string outText;
        if (result.Successful)
        {
            ArchipelagoConsole.LogMessage("Connected to Archipelago!");
            var success = (LoginSuccessful)result;

            ServerData.SetupSession(success.SlotData, session.RoomState.Seed);
            Authenticated = true;

            DeathLinkHandler = new(session.CreateDeathLinkService(), ServerData.SlotName);
            // two-way sync: push checks made offline, learn checks the server
            // already has (drives "already claimed" pickup labeling)
            session.Locations.CompleteLocationChecksAsync(ServerData.CheckedLocations.ToArray());
            foreach (long apid in session.Locations.AllLocationsChecked)
                if (!ServerData.CheckedLocations.Contains(apid))
                    ServerData.CheckedLocations.Add(apid);
            outText = $"Successfully connected to {ServerData.Uri} as {ServerData.SlotName}!";

            Locations = LoadLocationsFromFile();
            ItemFinder.InitializeItemTypes();
            ItemFinder.InitializeControlledBools();
            ScoutAllLocations();
            SaveSync.ReevaluateSeedBinding();

            ArchipelagoConsole.LogMessage(outText);
        }
        else
        {
            var failure = (LoginFailure)result;
            outText = $"Failed to connect to {ServerData.Uri} as {ServerData.SlotName}.";
            outText = failure.Errors.Aggregate(outText, (current, error) => current + $"\n    {error}");

            Plugin.BepinLogger.LogError(outText);

            Authenticated = false;
            Disconnect();
        }

        ArchipelagoConsole.LogMessage(outText);
        attemptingConnection = false;
    }

    /// <summary>
    /// something we wrong or we need to properly disconnect from the server. cleanup and re null our session
    /// </summary>
    private void Disconnect()
    {
        Plugin.BepinLogger.LogDebug("disconnecting from server...");
        session?.Socket.DisconnectAsync();
        session = null;
        Authenticated = false;
        goalSent = false;
    }

    public void SendMessage(string message)
    {
        session.Socket.SendPacketAsync(new SayPacket { Text = message });
    }

    /// <summary>
    /// report goal completion to the server (idempotent server-side; the
    /// guard just avoids spamming the packet once per replayed item)
    /// </summary>
    private void SendGoalCompletion()
    {
        if (goalSent || session == null) return;
        try
        {
            session.Socket.SendPacketAsync(new StatusUpdatePacket { Status = ArchipelagoClientState.ClientGoal });
            goalSent = true;
            ArchipelagoConsole.LogMessage("Goal complete! Victory reported to the server.");
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError(e);
        }
    }

    /// <summary>
    /// we received an item so reward it here
    /// </summary>
    /// <param name="helper">item helper which we can grab our item from</param>
    private void OnItemReceived(ReceivedItemsHelper helper)
    {
        // application happens on the main thread in ApplyPendingItems, indexed
        // against the current save's globals timeline (see SaveSync); here we
        // only drain the queue
        helper.DequeueItem();
    }

    /// <summary>
    /// called every frame from Plugin.Update: reaching the Ending Sequence
    /// scene means Edward Crowley is dead -> report the goal
    /// </summary>
    public void CheckGoalCompletion()
    {
        if (goalSent || !Authenticated || SaveSync.SeedMismatch) return;
        if (SceneManager.GetActiveScene().name == EndingSceneName)
            SendGoalCompletion();
    }

    /// <summary>
    /// apply every received item the current save's globals don't have yet.
    /// Runs once per frame on the main thread; SaveSync.AppliedIndex is
    /// rolled back/forward by save loads so items are applied exactly once
    /// per save timeline.
    /// </summary>
    public void ApplyPendingItems()
    {
        if (!Authenticated || session == null || !SaveSync.CanApplyItems) return;
        var items = session.Items.AllItemsReceived; // immutable snapshot
        while (SaveSync.AppliedIndex < items.Count)
        {
            var item = items[SaveSync.AppliedIndex];
            string name = session.Items.GetItemName(item.Item) ?? $"Item {item.Item}";
            try
            {
                ApplyItem(name);
            }
            catch (Exception e)
            {
                Plugin.BepinLogger.LogError($"Failed applying item '{name}': {e}");
            }
            SaveSync.AppliedIndex++;
        }
    }

    // amounts match the vanilla pickup FSMs' IntAdd values
    private static void ApplyItem(string name)
    {
        switch (name)
        {
            case "Small Med Kit": ItemFinder.AddToCounter("Heals Small", 1); break;
            case "Large Med Kit": ItemFinder.AddToCounter("Heals Large", 1); break;
            case "Antidote": ItemFinder.AddToCounter("Antidotes", 1); break;
            case "Handgun Ammo": ItemFinder.AddToCounter("Ammo in Box", 8); break;
            case "Shotgun Ammo": ItemFinder.AddToCounter("Shotgun in Box", 2); break;
            case "Magnum Ammo": ItemFinder.AddToCounter("Magnum in Box", 3); break;
            case "Grenade": ItemFinder.AddToCounter("Grenades", 1); break;
            case "Pocket Light": ItemFinder.SetGlobalBool("light found"); break;
            case "Handgun Laser Sight": ItemFinder.SetGlobalBool("Handgun Laser"); break;
        }
    }

    /// <summary>
    /// something went wrong with our socket connection
    /// </summary>
    /// <param name="e">thrown exception from our socket</param>
    /// <param name="message">message received from the server</param>
    private void OnSessionErrorReceived(Exception e, string message)
    {
        Plugin.BepinLogger.LogError(e);
        ArchipelagoConsole.LogMessage(message);
    }

    /// <summary>
    /// something went wrong closing our connection. disconnect and clean up
    /// </summary>
    /// <param name="reason"></param>
    private void OnSessionSocketClosed(string reason)
    {
        Plugin.BepinLogger.LogError($"Connection to Archipelago lost: {reason}");
        Disconnect();
    }

    public void SendLocationCheck(long apid)
    {
        try {
            if (!ServerData.CheckedLocations.Contains(apid))
                ServerData.CheckedLocations.Add(apid);
            session.Locations.CompleteLocationChecksAsync(apid);
        } catch (Exception e) {
            Plugin.BepinLogger.LogError(e);
        }
    }

    /// <summary>
    /// push checks recorded in a save's sidecar (made in an earlier session,
    /// possibly offline) to the server; no-op while disconnected because the
    /// connect handshake already sends all of ServerData.CheckedLocations
    /// </summary>
    public void ResendChecks(IEnumerable<long> apids)
    {
        if (!Authenticated || session == null) return;
        try {
            session.Locations.CompleteLocationChecksAsync(apids.ToArray());
        } catch (Exception e) {
            Plugin.BepinLogger.LogError(e);
        }
    }

    /// <summary>
    /// fetch what item every one of our locations holds, so checks can show
    /// a truthful popup the moment they fire
    /// </summary>
    private void ScoutAllLocations()
    {
        try {
            int ownSlot = session.ConnectionInfo.Slot;
            session.Locations.ScoutLocationsAsync(Locations.Keys.ToArray()).ContinueWith(t => {
                if (t.IsFaulted || t.Result?.Locations == null) {
                    Plugin.BepinLogger.LogError($"Location scout failed: {t.Exception}");
                    return;
                }
                var scouted = new Dictionary<long, ScoutedInfo>();
                foreach (NetworkItem item in t.Result.Locations) {
                    scouted[item.Location] = new ScoutedInfo {
                        ItemName = session.Items.GetItemName(item.Item) ?? $"Item {item.Item}",
                        PlayerName = session.Players.GetPlayerName(item.Player) ?? $"Player {item.Player}",
                        ForMe = item.Player == ownSlot,
                    };
                }
                ScoutedLocations = scouted;
                ArchipelagoConsole.LogMessage($"Scouted {scouted.Count} locations.");
            });
        } catch (Exception e) {
            Plugin.BepinLogger.LogError(e);
        }
    }

    /// <summary>
    /// show the game's native item toast for a just-checked location:
    /// our own items by name+icon, other players' items as "Sent: X -> Y".
    /// Re-grabbing an already-checked location (save reload respawns the
    /// world but checks can't roll back) grants nothing -- say so.
    /// </summary>
    public void ShowCheckPopup(long apid, bool alreadyChecked = false)
    {
        if (!ScoutedLocations.TryGetValue(apid, out ScoutedInfo info)) return;
        if (alreadyChecked) {
            ItemFinder.ShowItemPopup("Already claimed:", info.ItemName, ItemFinder.GenericIcon);
        } else if (info.ForMe) {
            int icon = ItemFinder.itemIcons.TryGetValue(info.ItemName, out int i) ? i : ItemFinder.GenericIcon;
            ItemFinder.ShowItemPopup("Obtained:", info.ItemName, icon);
        } else {
            ItemFinder.ShowItemPopup("Sent:", $"{info.ItemName} → {info.PlayerName}", ItemFinder.GenericIcon);
        }
    }

    private readonly string DATA_PATH = Path.Combine(Environment.CurrentDirectory, "BepInEx", "plugins", "APCrowCountry", "Data");

    private Dictionary<long, Location> LoadLocationsFromFile()
    {
        string locationsPath = Path.Combine(DATA_PATH, "locations.json");

        if (!File.Exists(locationsPath))
            throw new FileNotFoundException("Failed to load location data", locationsPath);

        string json = File.ReadAllText(locationsPath);

        return JsonConvert.DeserializeObject<Location[]>(json)
            .ToDictionary(x => x.apid, x => x);
    }
}