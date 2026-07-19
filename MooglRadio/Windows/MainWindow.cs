using System.Numerics;
using Dalamud.Bindings.ImGui; // NOTE: if this doesn't resolve, try `using ImGuiNET;` instead —
                                // Dalamud's ImGui binding package/namespace has changed across
                                // versions and this wasn't build-verified against a real install.
using Dalamud.Interface.Windowing;

namespace MooglRadio.Windows;

public sealed class MainWindow : Window
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("MOOGLradio###MooglRadioMainWindow")
    {
        this.plugin = plugin;
        Size = new Vector2(320, 160);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var nowPlaying = plugin.NowPlayingClient.Latest;

        ImGui.TextUnformatted(nowPlaying?.Dj is not null
            ? $"Live: {nowPlaying.Dj.Name}"
            : nowPlaying?.Block ?? "MOOGLradio");

        ImGui.TextWrapped(nowPlaying?.Track is not null
            ? $"{nowPlaying.Track.Artist} — {nowPlaying.Track.Title}"
            : "Loading now-playing info...");

        ImGui.Spacing();

        if (ImGui.Button(plugin.StreamPlayer.IsPlaying ? "Pause" : "Play"))
        {
            if (plugin.StreamPlayer.IsPlaying)
            {
                plugin.StreamPlayer.Stop();
            }
            else
            {
                plugin.StreamPlayer.Play(plugin.Configuration.StreamUrl);
            }
        }

        var volume = plugin.Configuration.Volume;
        if (ImGui.SliderFloat("Volume", ref volume, 0f, 1f))
        {
            plugin.Configuration.Volume = volume;
            plugin.StreamPlayer.Volume = volume;
            plugin.SaveConfiguration();
        }
    }
}
