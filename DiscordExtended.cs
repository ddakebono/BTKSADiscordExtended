using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ABI_RC.Core.InteractionSystem;
using ABI_RC.Core.Networking;
using ABI_RC.Core.Networking.API;
using ABI_RC.Core.Networking.API.Responses;
using ABI_RC.Core.Savior;
using ABI_RC.Helpers;
using ABI_RC.Systems.GameEventSystem;
using BTKSAImmersiveHud.Config;
using BTKUILib;
using BTKUILib.UIObjects;
using BTKUILib.UIObjects.Components;
using DiscordRPC;
using DiscordRPC.IO;
using DiscordRPC.Message;
using HarmonyLib;
using MelonLoader;
using Semver;
using Button = DiscordRPC.Button;
using RichPresence = ABI_RC.Core.Networking.RichPresence;

namespace BTKSADiscordExtended;

public static class BuildInfo
{
    public const string Name = "BTKSADiscordExtended";
    public const string Author = "DDAkebono";
    public const string Company = "BTK-Development";
    public const string Version = "1.0.2";
    public const string DownloadLink = "https://github.com/ddakebono/BTKSADiscordExtended/releases";
}

public class DiscordExtended : MelonMod
{
    internal static DiscordExtended Instance;
    internal static MelonLogger.Instance Logger;
    internal static Action OnUserLogin;
    internal static readonly List<IBTKBaseConfig> BTKConfigs = new();

    private WorldDetailsResponse _lastWorldDetails;
    private RichPresenceInstance_t _lastRPMsg;

    private BTKBoolConfig _displayWorldDetails = new(nameof(DiscordExtended), "Display World Details","Displays world information (name, privacy mode, player count, world image) of Friends Only or lower privacy instances, if this is off private instance details are hidden regardless of the private details setting",true, null, false);
    private BTKBoolConfig _displayWorldPrivate = new(nameof(DiscordExtended), "Display Private World Details","Displays world information (name, privacy mode, player count, world image) while in OwnerCanInvite and EveryoneCanInvite instances", false, null, false);
    private BTKBoolConfig _displayUsername = new(nameof(DiscordExtended), "Display Username", "Displays your username on the rich presence", false, null, false);
    private BTKBoolConfig _displayProfileButton = new(nameof(DiscordExtended), "Display Profile Button", "Displays a button on the rich presence that links to your profile on the ABI Hub", false,null, false);

    private bool _hasSetupUI;

    private static FieldInfo _discordEnabled = typeof(RichPresence).GetField("DiscordEnabled", BindingFlags.Static | BindingFlags.NonPublic);
    private static PropertyInfo _selfUsername = typeof(MetaPort).Assembly.GetType("ABI_RC.Core.Networking.AuthManager").GetProperty("Username", BindingFlags.Static | BindingFlags.Public);
    private static MethodInfo _btkGetCreatePageAdapter;

    //Discord stuff
    private static DiscordRpcClient _client;
    private static bool _isReady;
    private static bool _hasData;

    private static DiscordRPC.Assets _defaultAssets = new DiscordRPC.Assets
    {
        LargeImageKey = "discordrp-cvrmain",
    };

    private static DiscordRPC.RichPresence _presence = new()
    {
        State = "Starting up!",
        Details = "Just getting started...",
        Assets = _defaultAssets
    };

    private static Party _party;
    private static DiscordRPC.Assets _assets;
    private static Timestamps _timestamps;
    private static Button _userHubButton;

    public override void OnInitializeMelon()
    {
        Logger = LoggerInstance;

        Logger.Msg("BTK Standalone: Discord Extended - Starting Up");

        if (RegisteredMelons.Any(x => x.Info.Name.Equals("BTKCompanionLoader", StringComparison.OrdinalIgnoreCase)))
        {
            Logger.Msg("Hold on a sec! Looks like you've got BTKCompanion installed, this mod is built in and not needed!");
            Logger.Error("BTKSADiscordExtended has not started up! (BTKCompanion Running)");
            return;
        }

        if (!RegisteredMelons.Any(x => x.Info.Name.Equals("BTKUILib") && x.Info.SemanticVersion != null && x.Info.SemanticVersion.CompareTo(new SemVersion(1)) >= 0))
        {
            Logger.Error("BTKUILib was not detected or it outdated! BTKStandalone Mods cannot function without it!");
            Logger.Error("Please download an updated copy for BTKUILib!");
            return;
        }

        Instance = this;

        if (RegisteredMelons.Any(x => x.Info.Name.Equals("BTKUILib") && x.Info.SemanticVersion.CompareByPrecedence(new SemVersion(1, 9)) > 0))
        {
            //We're working with UILib 2.0.0, let's reflect the get create page function
            _btkGetCreatePageAdapter = typeof(Page).GetMethod("GetOrCreatePage", BindingFlags.Public | BindingFlags.Static);
            Logger.Msg($"BTKUILib 2.0.0 detected, attempting to grab GetOrCreatePage function: {_btkGetCreatePageAdapter != null}");
        }

        ApplyPatches(typeof(DiscordPatch));
        ApplyPatches(typeof(AuthManagerPatches));

        _displayUsername.OnConfigUpdated += o => { UpdatePresence(null, _hasData); };

        _displayWorldPrivate.OnConfigUpdated += o => { UpdatePresence(null, _hasData); };

        _displayWorldDetails.OnConfigUpdated += o => { UpdatePresence(null, _hasData); };

        _displayProfileButton.OnConfigUpdated += o => { UpdatePresence(null, _hasData); };

        CVRGameEventSystem.VRModeSwitch.OnPostSwitch.AddListener(_ => { UpdatePresence(null, _hasData); });

        OnUserLogin += () =>
        {
            //Let's setup our user button!
            _userHubButton = new Button
            {
                Label = $"{GetSelfUsername()}'s Profile",
                Url = $"https://hub.abinteractive.net/social/profile?guid={MetaPort.Instance.ownerId}"
            };
        };

        QuickMenuAPI.OnMenuRegenerate += OnMenuRegenerate;
    }

    internal void OnEnable(PresenceManager pm)
    {
        //Let's setup the discord client!
        if (_client != null) return;

        Logger.Msg("Preparing Discord client!");

        _client = new DiscordRpcClient(pm.applicationId, autoEvents: true, client: new ManagedNamedPipeClient());

        _client.OnReady += ClientOnOnReady;
        _client.OnClose += ClientOnOnClose;

        _client.Initialize();
    }

    internal void ClearPresence()
    {
        if (!GetDiscordEnabledState())
        {
            _client.SetPresence(null);
            return;
        }

        var detailsVrState = MetaPort.Instance.isUsingVr ? "VR" : "Desktop";

        _presence.Details = _displayUsername.BoolValue ? $"Chilling as {GetSelfUsername()} in {detailsVrState}" : $"Chilling in {detailsVrState}";
        _presence.State = "Exploring worlds or starting game.";
        _presence.Assets = _defaultAssets;
        _presence.Party = null;
        _presence.Timestamps = null;
        if (_userHubButton != null && _displayProfileButton.BoolValue)
            _presence.Buttons = new[] { _userHubButton };
        else
            _presence.Buttons = null;

        _client.SetPresence(_presence);
    }

    internal void UpdatePresence(RichPresenceInstance_t lastMsg, bool hasData)
    {
        if (!_isReady) return;

        if (!GetDiscordEnabledState()) return;

        _hasData = hasData;

        if (!hasData)
        {
            ClearPresence();
            return;
        }

        if (lastMsg != null)
            _lastRPMsg = lastMsg;

        if (_lastRPMsg == null) return;

        //Let's update the presence more betterer
        if (_lastWorldDetails == null || _lastWorldDetails.Id != MetaPort.Instance.CurrentWorldId && _displayWorldDetails.BoolValue)
        {
            var worldDetails = Task.Run(async () => await ApiConnection.MakeRequest<WorldDetailsResponse>(ApiConnection.ApiOperation.WorldDetail, new { worldID = MetaPort.Instance.CurrentWorldId }));
            worldDetails.ContinueWith((task, o) =>
            {
                if (!task.IsCompleted || task.IsFaulted) return;

                _lastWorldDetails = task.Result.Data;
                WorldDetailsResp(task.Result.Data);
            }, null);

            return;
        }

        WorldDetailsResp(_lastWorldDetails);
    }

    private void ApplyPatches(Type type)
    {
        try
        {
            HarmonyInstance.PatchAll(type);
        }
        catch(Exception e)
        {
            Logger.Msg($"Failed while patching {type.Name}!");
            Logger.Error(e);
        }
    }

    private void OnMenuRegenerate(CVR_MenuManager obj)
    {
        if (_hasSetupUI) return;
        _hasSetupUI = true;

        QuickMenuAPI.PrepareIcon("BTKStandalone", "BTKIcon", Assembly.GetExecutingAssembly().GetManifestResourceStream("BTKSADiscordExtended.Images.BTKIcon.png"));
        QuickMenuAPI.PrepareIcon("BTKStandalone", "Settings", Assembly.GetExecutingAssembly().GetManifestResourceStream("BTKSADiscordExtended.Images.Settings.png"));

        Page rootPage;

        if (_btkGetCreatePageAdapter != null)
            rootPage = (Page)_btkGetCreatePageAdapter.Invoke(null, new object[] { "BTKStandalone", "MainPage", true, "BTKIcon", null, false });
        else
            rootPage = new Page("BTKStandalone", "MainPage", true, "BTKIcon");

        rootPage.MenuTitle = "BTK Standalone Mods";
        rootPage.MenuSubtitle = "Toggle and configure your BTK Standalone mods here!";

        var functionToggles = rootPage.AddCategory("Discord Extended");

        var settingsPage = functionToggles.AddPage("DE Settings", "Settings", "Change settings related to Discord Extended", "BTKStandalone");

        var configCategories = new Dictionary<string, Category>();

        foreach (var config in BTKConfigs)
        {
            if (!configCategories.ContainsKey(config.Category))
                configCategories.Add(config.Category, settingsPage.AddCategory(config.Category));

            var cat = configCategories[config.Category];

            switch (config.Type)
            {
                case { } boolType when boolType == typeof(bool):
                    ToggleButton toggle = null;
                    var boolConfig = (BTKBoolConfig)config;
                    toggle = cat.AddToggle(config.Name, config.Description, boolConfig.BoolValue);
                    toggle.OnValueUpdated += b =>
                    {
                        if (!ConfigDialogs(config))
                            toggle.ToggleValue = boolConfig.BoolValue;

                        boolConfig.BoolValue = b;
                    };
                    break;
            }
        }
    }

    private bool ConfigDialogs(IBTKBaseConfig config)
    {
        if (config.DialogMessage != null)
        {
            QuickMenuAPI.ShowNotice("Notice", config.DialogMessage);
        }

        return true;
    }

    private void ClientOnOnClose(object sender, CloseMessage args)
    {
        Logger.Warning("Connection to Discord client lost!");
        _isReady = false;
    }

    private void ClientOnOnReady(object sender, ReadyMessage args)
    {
        Logger.Msg("Connected to discord client! Ready!");
        //Send our latest rich presence!
        _client.SetPresence(_presence);
        _isReady = true;
    }

    private void WorldDetailsResp(WorldDetailsResponse worldDetails)
    {
        var detailsVrState = MetaPort.Instance.isUsingVr ? "VR" : "Desktop";

        _presence.Details = _displayUsername.BoolValue ? $"Chilling as {GetSelfUsername()} in {detailsVrState}" : $"Chilling in {detailsVrState}";
        _presence.State = DisplayWorldDetails ? _lastRPMsg.InstanceName : "In Private Instance";

        _party ??= new Party();
        _party.ID = DisplayWorldDetails ? _lastRPMsg.InstanceMeshId : "";
        _party.Size = DisplayWorldDetails ? _lastRPMsg.CurrentPlayers : -1;
        _party.Max = DisplayWorldDetails ? _lastRPMsg.MaxPlayers : -1;
        _party.Privacy = Party.PrivacySetting.Public;
        _presence.Party = _party;

        _assets ??= new DiscordRPC.Assets();
        _assets.LargeImageKey = DisplayWorldDetails ? worldDetails.ImageUrl : "discordrp-cvrmain";
        _assets.LargeImageText = DisplayWorldDetails ? _lastRPMsg.InstancePrivacy : "";
        _presence.Assets = _assets;

        _timestamps ??= new Timestamps();
        _timestamps.StartUnixMilliseconds = (ulong?)RichPresence.LastConnectedToServer;
        _timestamps.EndUnixMilliseconds = null;
        _presence.Timestamps = _timestamps;

        if (_userHubButton != null && _displayProfileButton.BoolValue)
            _presence.Buttons = new[] { _userHubButton };
        else
            _presence.Buttons = null;

        _client.SetPresence(_presence);
    }

    private bool DisplayWorldDetails
    {
        get
        {
            if (_lastWorldDetails == null) return false;
            if (!_displayWorldDetails.BoolValue) return false;
            return _displayWorldPrivate.BoolValue || _lastRPMsg.InstancePrivacy is not ("OwnerMustInvite" or "EveryoneCanInvite");
        }
    }

    private static string GetSelfUsername()
    {
        return (string)_selfUsername.GetValue(null);
    }

    private static bool GetDiscordEnabledState()
    {
        return (bool)_discordEnabled.GetValue(null);
    }
}

[HarmonyPatch(typeof(LoginRoom))]
class AuthManagerPatches
{
    [HarmonyPatch("OnAuthenticationSuccess")]
    [HarmonyPostfix]
    private static void OnAuthSuccess()
    {
        DiscordExtended.OnUserLogin?.Invoke();
    }
}

[HarmonyPatch(typeof(PresenceManager))]
class DiscordPatch
{
    private static readonly FieldInfo RichPresenceLastMsgGetter = typeof(RichPresence).GetField("LastMsg", BindingFlags.Static | BindingFlags.NonPublic);

    private static RichPresenceInstance_t GetRichPresenceInfo()
    {
        return RichPresenceLastMsgGetter.GetValue(null) as RichPresenceInstance_t;
    }

    [HarmonyPatch(nameof(PresenceManager.UpdatePresence))]
    [HarmonyPrefix]
    static bool UpdatePresencePatch(string partyId = null)
    {
        var lastMsg = GetRichPresenceInfo();
        DiscordExtended.Instance.UpdatePresence(lastMsg, partyId != null);

        return false;
    }

    [HarmonyPatch("OnEnable")]
    [HarmonyPrefix]
    static bool EnablePatch(PresenceManager __instance)
    {
        DiscordExtended.Instance.OnEnable(__instance);

        return false;
    }

    [HarmonyPatch("OnDisable")]
    [HarmonyPrefix]
    static bool DisablePatch()
    {
        return false;
    }

    [HarmonyPatch(nameof(PresenceManager.ClearPresence))]
    [HarmonyPrefix]
    static bool ClearPresencePatch(PresenceManager __instance)
    {
        DiscordExtended.Instance.ClearPresence();

        return false;
    }
}