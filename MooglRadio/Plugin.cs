using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MooglRadio.Services;
using MooglRadio.Windows;

namespace MooglRadio;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/mooglradio";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IPluginLog log;
    private readonly MainWindow mainWindow;
    private readonly BgmMuter bgmMuter;

    public Configuration Configuration { get; }
    public WindowSystem WindowSystem { get; } = new("MooglRadio");
    public StreamPlayer StreamPlayer { get; } = new();
    public NowPlayingClient NowPlayingClient { get; } = new();

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log,
        IGameConfig gameConfig)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.log = log;
        this.bgmMuter = new BgmMuter(gameConfig, log);

        Configuration = this.pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // v1 configs (saved before the station domain was corrected) have
        // radio.moogl.ing baked in from an earlier scaffolding placeholder
        // that never actually resolved — force them onto the real moogl.fm
        // endpoints once, so existing installs don't stay stuck on 404s.
        if (Configuration.Version < 2)
        {
            Configuration.ApiBaseUrl = "https://moogl.fm";
            Configuration.StreamUrl = "https://moogl.fm/listen/mooglradio.mp3";
            Configuration.Version = 2;
            this.pluginInterface.SavePluginConfig(Configuration);
        }

        StreamPlayer.Volume = Configuration.Volume;
        StreamPlayer.Error += ex => this.log.Error(ex, "MOOGLradio playback error");
        StreamPlayer.Started += () =>
        {
            if (Configuration.MuteGameBgm)
            {
                bgmMuter.MuteForRadio();
            }
        };
        StreamPlayer.Stopped += bgmMuter.RestoreGameBgm;

        mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(mainWindow);

        this.commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the MOOGLradio player window. Subcommands: lock, unlock, ct (toggle click-through).",
        });

        this.pluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        this.pluginInterface.UiBuilder.OpenMainUi += () => mainWindow.IsOpen = true;

        NowPlayingClient.Start(Configuration.ApiBaseUrl);
    }

    public void SaveConfiguration() => pluginInterface.SavePluginConfig(Configuration);

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
        StreamPlayer.Dispose();
        NowPlayingClient.Dispose();
    }
}
