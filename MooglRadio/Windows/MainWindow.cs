using System.Numerics;
using Dalamud.Bindings.ImGui; // NOTE: if this doesn't resolve, try `using ImGuiNET;` instead —
                                // Dalamud's ImGui binding package/namespace has changed across
                                // versions and this wasn't build-verified against a real install.
using Dalamud.Interface.Windowing;

namespace MooglRadio.Windows;

/// <summary>
/// Compact, fixed-size now-playing widget. Not resizable by design — the
/// goal is a small always-there strip, not a full player UI. Pin/click-through
/// live in a gear-icon popup since a fully click-through window can't be
/// clicked to reach its own settings (see Plugin.OnCommand for the chat
/// fallback that unlocks it from outside the window).
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly Vector2 WindowSize = new(300, 110);

    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("MOOGLradio###MooglRadioMainWindow")
    {
        this.plugin = plugin;
        Size = WindowSize;
        SizeCondition = ImGuiCond.Always;
    }

    public override void PreDraw()
    {
        var config = plugin.Configuration;

        var flags = ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoCollapse;

        if (config.Locked)
        {
            flags |= ImGuiWindowFlags.NoMove;
        }

        if (config.ClickThrough)
        {
            flags |= ImGuiWindowFlags.NoInputs;
        }

        Flags = flags;
        ImGui.SetNextWindowBgAlpha(config.BackgroundAlpha);
    }

    public override void Draw()
    {
        var config = plugin.Configuration;
        var nowPlaying = plugin.NowPlayingClient.Latest;

        if (ImGui.Button(plugin.StreamPlayer.IsPlaying ? "⏸" : "▶", new Vector2(28, 28)))
        {
            if (plugin.StreamPlayer.IsPlaying)
            {
                plugin.StreamPlayer.Stop();
            }
            else
            {
                plugin.StreamPlayer.Play(config.StreamUrl);
            }
        }

        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextUnformatted(nowPlaying?.Dj is not null
            ? $"Live: {nowPlaying.Dj.Name}"
            : nowPlaying?.Block ?? "MOOGLradio");

        ImGui.TextWrapped(nowPlaying?.Track is not null
            ? $"{nowPlaying.Track.Artist} — {nowPlaying.Track.Title}"
            : "Loading now-playing info...");
        ImGui.EndGroup();

        ImGui.SameLine(WindowSize.X - 62);
        if (ImGui.Button("⚙", new Vector2(24, 24)))
        {
            ImGui.OpenPopup("MooglRadioSettings");
        }

        DrawSettingsPopup(config);

        ImGui.Spacing();

        var volume = config.Volume;
        ImGui.SetNextItemWidth(WindowSize.X - 24);
        if (ImGui.SliderFloat("##volume", ref volume, 0f, 1f, "Vol %.2f", ImGuiSliderFlags.AlwaysClamp))
        {
            config.Volume = volume;
            plugin.StreamPlayer.Volume = volume;
            plugin.SaveConfiguration();
        }
    }

    private void DrawSettingsPopup(Configuration config)
    {
        if (!ImGui.BeginPopup("MooglRadioSettings"))
        {
            return;
        }

        var locked = config.Locked;
        if (ImGui.Checkbox("Pin window", ref locked))
        {
            config.Locked = locked;
            plugin.SaveConfiguration();
        }

        var clickThrough = config.ClickThrough;
        if (ImGui.Checkbox("Click-through", ref clickThrough))
        {
            config.ClickThrough = clickThrough;
            plugin.SaveConfiguration();
        }

        if (config.ClickThrough)
        {
            ImGui.TextDisabled("Use /mooglradio ct to undo");
        }

        var alpha = config.BackgroundAlpha;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Opacity", ref alpha, 0.1f, 1f, "%.2f", ImGuiSliderFlags.AlwaysClamp))
        {
            config.BackgroundAlpha = alpha;
            plugin.SaveConfiguration();
        }

        ImGui.EndPopup();
    }
}
