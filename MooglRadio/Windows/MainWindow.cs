using System.Numerics;
using Dalamud.Bindings.ImGui; // NOTE: if this doesn't resolve, try `using ImGuiNET;` instead —
                                // Dalamud's ImGui binding package/namespace has changed across
                                // versions and this wasn't build-verified against a real install.
using Dalamud.Interface.Windowing;
using MooglRadio.Models;

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
    private static readonly Vector2 WindowSize = new(320, 150);
    private static readonly Vector2 ArtSize = new(48, 48);
    private const float TextColumnWidth = 230f;
    private const float MarqueeSpeedPxPerSecond = 40f;
    private const float MarqueeGap = 40f;

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
        var track = nowPlaying?.Track;

        DrawArt();

        ImGui.SameLine();
        ImGui.BeginGroup();
        DrawNowPlayingText(nowPlaying, track);
        ImGui.EndGroup();

        ImGui.SameLine(WindowSize.X - 34);
        if (ImGui.Button("...", new Vector2(24, 24)))
        {
            ImGui.OpenPopup("MooglRadioSettings");
        }

        DrawSettingsPopup(config);

        if (plugin.StreamPlayer.LastError is { } playError)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), $"Playback error: {playError}");
        }

        ImGui.Spacing();

        if (ImGui.Button(plugin.StreamPlayer.IsPlaying ? "Pause" : "Play", new Vector2(52, 28)))
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
        var volume = config.Volume;
        ImGui.SetNextItemWidth(WindowSize.X - 52 - 16 - 8);
        if (ImGui.SliderFloat("##volume", ref volume, 0f, 1f, "Vol %.2f", ImGuiSliderFlags.AlwaysClamp))
        {
            config.Volume = volume;
            plugin.StreamPlayer.Volume = volume;
            plugin.SaveConfiguration();
        }
    }

    private void DrawArt()
    {
        var texture = plugin.AlbumArtService.Texture;
        if (texture is not null)
        {
            ImGui.Image(texture.Handle, ArtSize);
        }
        else
        {
            // Reserve the same layout space so text doesn't jump around
            // when art loads in or a track has none.
            ImGui.Dummy(ArtSize);
        }
    }

    private void DrawNowPlayingText(NowPlaying? nowPlaying, NowPlayingTrack? track)
    {
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        var dimColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        var errorColor = ImGui.GetColorU32(new Vector4(1f, 0.5f, 0.5f, 1f));

        var headerText = nowPlaying?.Dj is not null ? $"Live: {nowPlaying.Dj.Name}" : nowPlaying?.Block ?? "MOOGLradio";
        DrawMarquee(headerText, textColor);

        if (track is not null)
        {
            DrawMarquee($"{track.Artist} — {track.Title}", textColor);
            if (!string.IsNullOrWhiteSpace(track.Album))
            {
                DrawMarquee(track.Album, dimColor);
            }
        }
        else if (plugin.NowPlayingClient.LastError is { } metaError)
        {
            DrawMarquee($"Can't reach MOOGLradio ({metaError})", errorColor);
        }
        else
        {
            ImGui.TextUnformatted("Loading now-playing info...");
        }
    }

    /// <summary>
    /// Draws a single line of text clipped to <see cref="TextColumnWidth"/>,
    /// scrolling it horizontally on a loop when it's too wide to fit —
    /// avoids the wrapped/cramped look of long titles in a window that's
    /// deliberately not resizable. Text that already fits just draws
    /// normally, no motion.
    /// </summary>
    private static void DrawMarquee(string text, uint colorU32)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var lineHeight = ImGui.GetTextLineHeight();
        var textSize = ImGui.CalcTextSize(text);

        ImGui.Dummy(new Vector2(TextColumnWidth, lineHeight));

        if (textSize.X <= TextColumnWidth)
        {
            drawList.AddText(pos, colorU32, text);
            return;
        }

        drawList.PushClipRect(pos, pos + new Vector2(TextColumnWidth, lineHeight), true);
        var cycle = textSize.X + MarqueeGap;
        var offset = (float)(ImGui.GetTime() * MarqueeSpeedPxPerSecond % cycle);
        drawList.AddText(pos - new Vector2(offset, 0), colorU32, text);
        drawList.AddText(pos + new Vector2(cycle - offset, 0), colorU32, text);
        drawList.PopClipRect();
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

        var muteGameBgm = config.MuteGameBgm;
        if (ImGui.Checkbox("Mute game music while playing", ref muteGameBgm))
        {
            plugin.SetMuteGameBgm(muteGameBgm);
        }

        ImGui.EndPopup();
    }
}
