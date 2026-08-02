using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MooglRadio.Models;
using MooglRadio.Services;
using MooglRadio.Windows;

namespace MooglRadio;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/mooglradio";
    private const string DefaultApiBaseUrl = "https://moogl.fm";
    private const string DefaultStreamUrl = "https://moogl.fm/listen/mooglradio.mp3";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IPluginLog log;
    private readonly IChatGui chatGui;
    private readonly IClientState clientState;
    private readonly MainWindow mainWindow;
    private readonly BgmMuter bgmMuter;

    /// <summary>Identity of the last track a chat notification was printed for, so
    /// <see cref="OnNowPlayingUpdated"/> only fires on an actual change, not every poll.</summary>
    private string? lastNotifiedTrackKey;

    /// <summary>Last block title a chat notification was printed for. See <see cref="lastNotifiedTrackKey"/>.</summary>
    private string? lastNotifiedBlock;

    /// <summary>Suppresses notifications for the state already in effect when the plugin loads/reconnects,
    /// so the first poll doesn't print a "change" for whatever was already playing.</summary>
    private bool hasSeenFirstNowPlaying;

    public Configuration Configuration { get; }
    public WindowSystem WindowSystem { get; } = new("MooglRadio");
    public StreamPlayer StreamPlayer { get; } = new();
    public NowPlayingClient NowPlayingClient { get; } = new();
    public AlbumArtService AlbumArtService { get; }
    public ListenerLocationClient ListenerLocationClient { get; } = new();

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log,
        IGameConfig gameConfig,
        ITextureProvider textureProvider,
        IChatGui chatGui,
        IClientState clientState)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.log = log;
        this.chatGui = chatGui;
        this.clientState = clientState;
        this.bgmMuter = new BgmMuter(gameConfig, log);
        this.AlbumArtService = new AlbumArtService(textureProvider, log);

        Configuration = this.pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // v1 configs (saved before the station domain was corrected) have
        // radio.moogl.ing baked in from an earlier scaffolding placeholder
        // that never actually resolved — force them onto the real moogl.fm
        // endpoints once, so existing installs don't stay stuck on 404s.
        if (Configuration.Version < 2)
        {
            Configuration.ApiBaseUrl = DefaultApiBaseUrl;
            Configuration.StreamUrl = DefaultStreamUrl;
            Configuration.Version = 2;
            this.pluginInterface.SavePluginConfig(Configuration);
        }

        // Defense in depth: ApiBaseUrl/StreamUrl aren't exposed for editing
        // in the plugin's own settings UI, but the saved config is still a
        // plain JSON file on disk — nothing stops it being hand-edited (or
        // written by something else with file access) to downgrade to
        // http:// or repoint at an arbitrary host. Reject anything that
        // isn't a well-formed https:// URL before it's ever used to make a
        // request, falling back to the known-good default instead.
        var configChanged = false;
        if (!IsValidHttpsUrl(Configuration.ApiBaseUrl))
        {
            this.log.Warning($"MOOGLradio: config ApiBaseUrl '{Configuration.ApiBaseUrl}' is not a valid https URL, resetting to default");
            Configuration.ApiBaseUrl = DefaultApiBaseUrl;
            configChanged = true;
        }

        if (!IsValidHttpsUrl(Configuration.StreamUrl))
        {
            this.log.Warning($"MOOGLradio: config StreamUrl '{Configuration.StreamUrl}' is not a valid https URL, resetting to default");
            Configuration.StreamUrl = DefaultStreamUrl;
            configChanged = true;
        }

        if (configChanged)
        {
            this.pluginInterface.SavePluginConfig(Configuration);
        }

        StreamPlayer.Volume = Configuration.Volume;
        StreamPlayer.Error += ex => this.log.Error(ex, "MOOGLradio playback error");
        StreamPlayer.Diagnostic += msg => this.log.Info($"MOOGLradio: {msg}");
        StreamPlayer.Started += () =>
        {
            if (Configuration.MuteGameBgm)
            {
                bgmMuter.MuteForRadio();
            }
        };
        StreamPlayer.Stopped += bgmMuter.RestoreGameBgm;
        StreamPlayer.Started += () =>
        {
            if (Configuration.ShareListenerLocation)
            {
                ListenerLocationClient.Start(Configuration.ApiBaseUrl, GetCurrentLocation);
            }
        };
        StreamPlayer.Stopped += ListenerLocationClient.Stop;

        NowPlayingClient.Updated += np => AlbumArtService.UpdateFor(Configuration.ApiBaseUrl, np.Track?.ArtUrl);
        NowPlayingClient.Updated += OnNowPlayingUpdated;

        mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(mainWindow);

        this.commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the MOOGLradio player window. Subcommands: lock, unlock, ct (toggle click-through), mini (toggle mini player).",
        });

        this.pluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        this.pluginInterface.UiBuilder.OpenMainUi += () => mainWindow.IsOpen = true;

        NowPlayingClient.Start(Configuration.ApiBaseUrl);
    }

    public void SaveConfiguration() => pluginInterface.SavePluginConfig(Configuration);

    private static bool IsValidHttpsUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Updates the mute-game-BGM setting, applying it immediately if the radio is already playing.</summary>
    public void SetMuteGameBgm(bool value)
    {
        Configuration.MuteGameBgm = value;
        SaveConfiguration();

        if (!StreamPlayer.IsPlaying)
        {
            return;
        }

        if (value)
        {
            bgmMuter.MuteForRadio();
        }
        else
        {
            bgmMuter.RestoreGameBgm();
        }
    }

    /// <summary>Updates the share-listener-location setting, starting or stopping the
    /// heartbeat loop immediately if the stream is already playing rather than waiting
    /// for the next Play()/Stop() cycle.</summary>
    public void SetShareListenerLocation(bool value)
    {
        Configuration.ShareListenerLocation = value;
        SaveConfiguration();

        if (!StreamPlayer.IsPlaying)
        {
            return;
        }

        if (value)
        {
            ListenerLocationClient.Start(Configuration.ApiBaseUrl, GetCurrentLocation);
        }
        else
        {
            ListenerLocationClient.Stop();
        }
    }

    /// <summary>Raw world position + zone for the current heartbeat, or null when there's
    /// no local player to read (e.g. between zone loads). Deliberately raw world coordinates
    /// (<see cref="IPlayerCharacter.Position"/>), not the in-game map-pixel readout — the API
    /// converts world space to map space itself.</summary>
    private (int territoryId, float x, float z)? GetCurrentLocation()
    {
        if (clientState.LocalPlayer is not { } player)
        {
            return null;
        }

        return ((int)clientState.TerritoryType, player.Position.X, player.Position.Z);
    }

    /// <summary>Prints chat notifications for track/block changes, per <see cref="Configuration.ChatNotifyTrackChange"/>
    /// and <see cref="Configuration.ChatNotifyBlockChange"/>. Compares against the last-notified state rather than
    /// trusting every poll to represent a change, since <see cref="NowPlayingClient"/> fires Updated on each poll
    /// regardless of whether the payload actually changed.</summary>
    private void OnNowPlayingUpdated(NowPlaying nowPlaying)
    {
        var suppress = !hasSeenFirstNowPlaying;
        hasSeenFirstNowPlaying = true;

        var track = nowPlaying.Track;
        var trackKey = track is null ? null : $"{track.Title} {track.Artist} {track.Album}";

        if (Configuration.ChatNotifyTrackChange
            && track is not null
            && trackKey != lastNotifiedTrackKey
            && !suppress)
        {
            chatGui.Print($"MOOGL Radio: {track.Title} by {track.Artist} on {track.Album}");
        }

        lastNotifiedTrackKey = trackKey;

        var block = string.IsNullOrWhiteSpace(nowPlaying.Block) ? null : nowPlaying.Block;

        if (Configuration.ChatNotifyBlockChange && block != lastNotifiedBlock && !suppress)
        {
            if (lastNotifiedBlock is not null)
            {
                chatGui.Print($"MOOGL Radio: {lastNotifiedBlock} had ended");
            }

            if (block is not null)
            {
                chatGui.Print($"MOOGL Radio: {block} has started");
            }
        }

        lastNotifiedBlock = block;
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "lock":
                Configuration.Locked = true;
                SaveConfiguration();
                break;
            case "unlock":
                Configuration.Locked = false;
                SaveConfiguration();
                break;
            case "ct":
            case "clickthrough":
                Configuration.ClickThrough = !Configuration.ClickThrough;
                SaveConfiguration();
                break;
            case "mini":
                Configuration.MiniPlayer = !Configuration.MiniPlayer;
                SaveConfiguration();
                break;
            default:
                mainWindow.Toggle();
                break;
        }
    }

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        WindowSystem.RemoveAllWindows();
        commandManager.RemoveHandler(CommandName);
        NowPlayingClient.Updated -= OnNowPlayingUpdated;
        StreamPlayer.Dispose();
        NowPlayingClient.Dispose();
        AlbumArtService.Dispose();
        ListenerLocationClient.Dispose();
    }
}
