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

    public Configuration Configuration { get; }
    public WindowSystem WindowSystem { get; } = new("MooglRadio");
    public StreamPlayer StreamPlayer { get; } = new();
    public NowPlayingClient NowPlayingClient { get; } = new();

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.log = log;

        Configuration = this.pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        StreamPlayer.Volume = Configuration.Volume;
        StreamPlayer.Error += ex => this.log.Error(ex, "MOOGLradio playback error");

        mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(mainWindow);

        this.commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the MOOGLradio player window.",
        });

        this.pluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        this.pluginInterface.UiBuilder.OpenMainUi += () => mainWindow.IsOpen = true;

        NowPlayingClient.Start(Configuration.ApiBaseUrl);
    }

    public void SaveConfiguration() => pluginInterface.SavePluginConfig(Configuration);

    private void OnCommand(string command, string args) => mainWindow.Toggle();

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        WindowSystem.RemoveAllWindows();
        commandManager.RemoveHandler(CommandName);
        StreamPlayer.Dispose();
        NowPlayingClient.Dispose();
    }
}
