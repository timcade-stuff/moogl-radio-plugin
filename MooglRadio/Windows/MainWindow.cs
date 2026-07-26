using System.Numerics;
using Dalamud.Bindings.ImGui; // NOTE: if this doesn't resolve, try `using ImGuiNET;` instead —
                                // Dalamud's ImGui binding package/namespace has changed across
                                // versions and this wasn't build-verified against a real install.
using Dalamud.Interface.Windowing;
using MooglRadio.Models;

namespace MooglRadio.Windows;

/// <summary>
/// Compact, fixed-size now-playing widget. Not resizable by design — the
/// goal is a small always-there strip, not a full player UI. Visual
/// language (dark glass card, purple/blue accent, pill toggles) matches
/// the moogl.fm site mockup, see <see cref="Theme"/> and <see cref="Icons"/>.
/// Pin/click-through live in a gear-icon popup since a fully click-through
/// window can't be clicked to reach its own settings (see Plugin.OnCommand
/// for the chat fallback that unlocks it from outside the window).
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly Vector2 WindowSize = new(320, 190);
    private static readonly Vector2 ArtSize = new(60, 60);
    private const float CardPadding = 14f;
    private const float TextColumnWidth = 172f;
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
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
    }

    public override void Draw()
    {
        var config = plugin.Configuration;
        var nowPlaying = plugin.NowPlayingClient.Latest;
        var track = nowPlaying?.Track;

        // WindowPadding is zeroed in PreDraw, so content starts exactly at
        // the window's content origin (just below the native title bar)
        // and spans its full width/height — no gaps, no overflow.
        var cardMin = ImGui.GetCursorScreenPos();
        var cardMax = cardMin + ImGui.GetContentRegionAvail();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(cardMin, cardMax, Theme.U32(Theme.BgCard), Theme.CardRounding);
        dl.AddRect(cardMin, cardMax, Theme.U32(Theme.BorderColor), Theme.CardRounding);

        ImGui.SetCursorScreenPos(cardMin + new Vector2(CardPadding, CardPadding));
        ImGui.BeginGroup();

        DrawHeaderRow(config, nowPlaying);
        ImGui.Dummy(new Vector2(1, 10));

        ImGui.BeginGroup();
        DrawArt();
        ImGui.EndGroup();
        ImGui.SameLine();
        ImGui.BeginGroup();
        DrawNowPlayingText(track);
        ImGui.EndGroup();

        if (plugin.StreamPlayer.LastError is { } playError)
        {
            ImGui.Dummy(new Vector2(1, 4));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + WindowSize.X - CardPadding * 2);
            ImGui.TextColored(Theme.ErrorColor, $"Playback error: {playError}");
            ImGui.PopTextWrapPos();
        }

        ImGui.Dummy(new Vector2(1, 12));
        DrawTransportRow(config);

        ImGui.Dummy(new Vector2(1, 10));
        DrawFooterRow();

        ImGui.EndGroup();

        DrawSettingsPopup(config);
    }

    private void DrawHeaderRow(Configuration config, NowPlaying? nowPlaying)
    {
        var badgeLabel = nowPlaying?.Dj is not null ? $"Live · {nowPlaying.Dj.Name}" : "moogl.fm";
        var textSize = ImGui.CalcTextSize(badgeLabel);
        var badgeSize = new Vector2(textSize.X + 22, 20);
        var badgeMin = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        dl.AddRectFilled(badgeMin, badgeMin + badgeSize, Theme.U32(Theme.AccentMutedBg), Theme.PillRounding);
        dl.AddRect(badgeMin, badgeMin + badgeSize, Theme.U32(Theme.AccentMutedBorder), Theme.PillRounding);
        var dotCenter = badgeMin + new Vector2(11, badgeSize.Y / 2);
        dl.AddCircleFilled(dotCenter, 3f, Theme.U32(Theme.Success), 12);
        dl.AddText(badgeMin + new Vector2(18, (badgeSize.Y - textSize.Y) / 2), Theme.U32(Theme.AccentSecondary), badgeLabel);

        ImGui.Dummy(badgeSize);

        var iconSize = new Vector2(24, 24);
        // Local x is relative to the window's left edge (WindowPadding is
        // zeroed for this window), so this lines the icons up flush with
        // the card's right inset regardless of the badge's width.
        ImGui.SameLine(WindowSize.X - CardPadding - iconSize.X * 2 - 4);

        if (Widgets.IconButton("pin", iconSize, (dl2, c, s, col) => Icons.Pin(dl2, c, s, col, config.Locked), config.Locked))
        {
            config.Locked = !config.Locked;
            plugin.SaveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(config.Locked ? "Unlock window position" : "Lock window position");
        }

        ImGui.SameLine(0, 4);

        if (Widgets.IconButton("gear", iconSize, Icons.Gear))
        {
            ImGui.OpenPopup("MooglRadioSettings");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Settings");
        }
    }

    private void DrawArt()
    {
        var texture = plugin.AlbumArtService.Texture;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        if (texture is not null)
        {
            dl.AddImageRounded(texture.Handle, pos, pos + ArtSize, Vector2.Zero, Vector2.One, Theme.U32(new Vector4(1, 1, 1, 1)), 8f);
        }
        else
        {
            dl.AddRectFilled(pos, pos + ArtSize, Theme.U32(Theme.TrackBg), 8f);
            dl.AddRect(pos, pos + ArtSize, Theme.U32(Theme.BorderColor), 8f);
            Icons.MusicNote(dl, pos + ArtSize / 2, ArtSize.X, Theme.U32(Theme.TextMuted));
        }

        // Reserve the same layout space either way so text doesn't jump
        // around when art loads in or a track has none.
        ImGui.Dummy(ArtSize);
    }

    private void DrawNowPlayingText(NowPlayingTrack? track)
    {
        var textColor = Theme.U32(Theme.TextPrimary);
        var dimColor = Theme.U32(Theme.TextMuted);
        var errorColor = Theme.U32(Theme.ErrorColor);

        ImGui.Dummy(new Vector2(1, 2));

        if (track is not null)
        {
            DrawMarquee(track.Title, textColor);
            DrawMarquee(track.Artist, dimColor);
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

    private void DrawTransportRow(Configuration config)
    {
        var dl = ImGui.GetWindowDrawList();
        const float buttonSize = 40f;

        var btnMin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##playpause", new Vector2(buttonSize, buttonSize));
        var clicked = ImGui.IsItemClicked();
        var center = btnMin + new Vector2(buttonSize / 2, buttonSize / 2);
        dl.AddCircleFilled(center, buttonSize / 2, Theme.U32(Theme.AccentPrimary), 32);

        var isPlaying = plugin.StreamPlayer.IsPlaying;
        if (isPlaying)
        {
            Icons.Pause(dl, center, buttonSize, Theme.U32(new Vector4(1, 1, 1, 1)));
        }
        else
        {
            Icons.Play(dl, center, buttonSize, Theme.U32(new Vector4(1, 1, 1, 1)));
        }

        if (clicked)
        {
            if (isPlaying)
            {
                plugin.StreamPlayer.Stop();
            }
            else
            {
                plugin.StreamPlayer.Play(config.StreamUrl);
            }
        }

        ImGui.SameLine(0, 14);
        ImGui.BeginGroup();

        var speakerPos = ImGui.GetCursorScreenPos();
        Icons.Speaker(dl, speakerPos + new Vector2(9, 9), 20, Theme.U32(Theme.TextMuted));
        ImGui.Dummy(new Vector2(20, 20));
        ImGui.SameLine();

        var sliderWidth = WindowSize.X - CardPadding * 2 - buttonSize - 14 - 20 - 8;
        DrawSlider("##volume", config.Volume, sliderWidth, out var newVolume, Theme.AccentSecondary);
        if (newVolume != config.Volume)
        {
            config.Volume = newVolume;
            plugin.StreamPlayer.Volume = newVolume;
            plugin.SaveConfiguration();
        }

        ImGui.EndGroup();
    }

    private void DrawFooterRow()
    {
        ImGui.PushStyleColor(ImGuiCol.Separator, Theme.BorderColor);
        ImGui.Separator();
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(1, 4));
        ImGui.TextColored(Theme.TextMuted, "MOOGL Radio · moogl.fm");
    }

    /// <summary>
    /// A 0..1 slider styled like the mockup's pill sliders (flat track +
    /// accent-colored fill), driven by a plain click/drag InvisibleButton
    /// instead of ImGui's default slider visuals.
    /// </summary>
    private static void DrawSlider(string id, float value, float width, out float newValue, Vector4 fillColor)
    {
        const float height = 4f;
        var pos = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, 16);
        ImGui.PushID(id);
        ImGui.InvisibleButton("##slider", size);
        var active = ImGui.IsItemActive();
        newValue = value;

        if (active)
        {
            var rect = ImGui.GetItemRectMin();
            var pct = (ImGui.GetIO().MousePos.X - rect.X) / width;
            newValue = System.Math.Clamp(pct, 0f, 1f);
        }

        var dl = ImGui.GetWindowDrawList();
        var trackMin = pos + new Vector2(0, (size.Y - height) / 2);
        var trackMax = trackMin + new Vector2(width, height);
        dl.AddRectFilled(trackMin, trackMax, Theme.U32(Theme.TrackBg), height / 2);
        dl.AddRectFilled(trackMin, trackMin + new Vector2(width * newValue, height), Theme.U32(fillColor), height / 2);

        var thumbCenter = new Vector2(trackMin.X + width * newValue, pos.Y + size.Y / 2);
        dl.AddCircleFilled(thumbCenter, 6f, Theme.U32(Theme.TextPrimary), 16);
        ImGui.PopID();
    }

    private void DrawSettingsPopup(Configuration config)
    {
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Theme.BgCard);
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.BorderColor);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, Theme.CardRounding);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));

        if (ImGui.BeginPopup("MooglRadioSettings"))
        {
            ImGui.TextColored(Theme.TextPrimary, "Settings");
            ImGui.Dummy(new Vector2(1, 8));

            SectionHeader("Playback");
            ImGui.TextColored(Theme.TextSecondary, "Volume");
            ImGui.SameLine(90);
            DrawSlider("##volume-settings", config.Volume, 160, out var newVolume, Theme.AccentPrimary);
            if (newVolume != config.Volume)
            {
                config.Volume = newVolume;
                plugin.StreamPlayer.Volume = newVolume;
                plugin.SaveConfiguration();
            }

            ImGui.SameLine();
            ImGui.TextColored(Theme.TextMuted, $"{(int)System.MathF.Round(config.Volume * 100)}%");

            ImGui.Dummy(new Vector2(1, 12));
            SectionHeader("Window");

            ImGui.TextColored(Theme.TextSecondary, "Opacity");
            ImGui.SameLine(90);
            DrawSlider("##opacity", config.BackgroundAlpha, 160, out var newAlpha, Theme.AccentSecondary);
            if (newAlpha != config.BackgroundAlpha)
            {
                config.BackgroundAlpha = newAlpha;
                plugin.SaveConfiguration();
            }

            ImGui.SameLine();
            ImGui.TextColored(Theme.TextMuted, $"{(int)System.MathF.Round(config.BackgroundAlpha * 100)}%");

            ImGui.Dummy(new Vector2(1, 10));

            ToggleRow(
                "Click-through when unfocused",
                "Clicks pass to the game when not hovered",
                config.ClickThrough,
                v =>
                {
                    config.ClickThrough = v;
                    plugin.SaveConfiguration();
                });

            if (config.ClickThrough)
            {
                ImGui.TextColored(Theme.TextMuted, "Use /mooglradio ct to undo");
                ImGui.Dummy(new Vector2(1, 4));
            }

            ToggleRow(
                "Lock window position",
                "Prevents accidental dragging in combat",
                config.Locked,
                v =>
                {
                    config.Locked = v;
                    plugin.SaveConfiguration();
                });

            ToggleRow(
                "Mute game music while playing",
                "Restores game BGM when the stream stops",
                config.MuteGameBgm,
                v => plugin.SetMuteGameBgm(v));

            ImGui.EndPopup();
        }

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    private static void SectionHeader(string label)
    {
        ImGui.TextColored(Theme.TextMuted, label.ToUpperInvariant());
        ImGui.Dummy(new Vector2(1, 4));
    }

    private static void ToggleRow(string label, string description, bool value, System.Action<bool> onChange)
    {
        ImGui.PushStyleColor(ImGuiCol.Separator, Theme.BorderColor);
        ImGui.Separator();
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(1, 6));

        var cursorY = ImGui.GetCursorPosY();
        ImGui.TextColored(Theme.TextPrimary, label);
        ImGui.TextColored(Theme.TextMuted, description);

        ImGui.SameLine(260);
        ImGui.SetCursorPosY(cursorY);
        if (Widgets.ToggleSwitch(label, value))
        {
            onChange(!value);
        }

        ImGui.Dummy(new Vector2(1, 4));
    }
}
