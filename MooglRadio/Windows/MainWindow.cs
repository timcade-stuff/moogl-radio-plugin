using System;
using System.Numerics;
using Dalamud.Bindings.ImGui; // NOTE: if this doesn't resolve, try `using ImGuiNET;` instead —
                                // Dalamud's ImGui binding package/namespace has changed across
                                // versions and this wasn't build-verified against a real install.
using Dalamud.Interface.Windowing;
using MooglRadio.Models;

namespace MooglRadio.Windows;

/// <summary>
/// Compact, fixed-width now-playing widget (height auto-fits its content
/// via <see cref="ImGuiWindowFlags.AlwaysAutoResize"/> so layout changes
/// can't clip content against a stale hardcoded size). Visual language
/// (dark glass card, purple/blue accent, pill toggles) matches the
/// moogl.fm site mockup, see <see cref="Theme"/> and <see cref="Icons"/>.
///
/// Chrome is entirely custom — <see cref="ImGuiWindowFlags.NoTitleBar"/> is
/// set and the header row draws its own pin/gear/close icons, since the
/// native Dalamud title bar (with its hamburger menu) didn't match the
/// site's look. Dragging is reimplemented by hand in <see cref="Draw"/>
/// since there's no native title bar left to drag by.
///
/// Click-through (<see cref="Configuration.ClickThrough"/>) disables all
/// window input via <see cref="ImGuiWindowFlags.NoInputs"/>, which would
/// otherwise strand the user with no way to reach the gear icon again.
/// Holding Ctrl while hovering the window temporarily lifts that flag for
/// the frame (see <see cref="WantsClickThroughOverride"/>) — the chat
/// command fallback in Plugin.OnCommand still exists as a backup.
///
/// <see cref="Configuration.BackgroundAlpha"/> ("Opacity" in settings)
/// fades the entire card — background, border, icons, art, text — via
/// <see cref="opacity"/>/<see cref="C"/>/<see cref="CV"/>, not just the
/// (invisible, fully-covered) native window background.
/// </summary>
public sealed class MainWindow : Window
{
    private const float WindowWidth = 320f;
    private static readonly Vector2 ArtSize = new(60, 60);
    private const float CardPadding = 14f;
    private const float TextColumnWidth = 172f;
    private const float MarqueeSpeedPxPerSecond = 40f;
    private const float MarqueeGap = 40f;

    private readonly Plugin plugin;

    private Vector2 lastWindowMin;
    private Vector2 lastWindowMax;
    private bool hasWindowRect;
    private bool clickThroughOverrideActive;

    /// <summary>Current frame's opacity multiplier (== config.BackgroundAlpha),
    /// forced to 1 while the settings popup draws so it stays legible
    /// regardless of how transparent the main card is. See <see cref="C"/>.</summary>
    private float opacity = 1f;

    public MainWindow(Plugin plugin)
        : base("MOOGLradio###MooglRadioMainWindow")
    {
        this.plugin = plugin;
    }

    public override void PreDraw()
    {
        var config = plugin.Configuration;

        var flags = ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoTitleBar;

        clickThroughOverrideActive = config.ClickThrough && WantsClickThroughOverride();
        if (config.ClickThrough && !clickThroughOverrideActive)
        {
            flags |= ImGuiWindowFlags.NoInputs;
        }

        Flags = flags;

        // The rounded card drawn in Draw() is the only visible background —
        // it reads config.BackgroundAlpha itself (see `opacity`/C/CV). The
        // native window bg sits behind it and would otherwise show through
        // as an unrounded, unfaded square, so keep it fully transparent and
        // borderless instead.
        ImGui.SetNextWindowBgAlpha(0f);
        ImGui.SetNextWindowSizeConstraints(new Vector2(WindowWidth, 0), new Vector2(WindowWidth, float.MaxValue));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(2);
    }

    /// <summary>
    /// True when the user is holding Ctrl with the mouse over where the
    /// window was last frame — checked in <see cref="PreDraw"/>, before
    /// this frame's window rect exists, so it necessarily lags one frame.
    /// That's imperceptible in practice and avoids needing input state
    /// from a window that (this frame) may itself be ignoring input.
    /// </summary>
    private bool WantsClickThroughOverride()
    {
        if (!hasWindowRect)
        {
            return false;
        }

        var ctrlHeld = ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl);
        if (!ctrlHeld)
        {
            return false;
        }

        var mouse = ImGui.GetIO().MousePos;
        return mouse.X >= lastWindowMin.X && mouse.X <= lastWindowMax.X
            && mouse.Y >= lastWindowMin.Y && mouse.Y <= lastWindowMax.Y;
    }

    /// <summary>Bakes <see cref="opacity"/> into a color for ImDrawList calls
    /// (AddRectFilled, AddText, etc.), which don't participate in ImGui's own
    /// style alpha since they write raw pixel colors.</summary>
    private uint C(Vector4 color) => Theme.U32(Theme.Fade(color, opacity));

    /// <summary>Same as <see cref="C"/> but for ImGui.TextColored, which wants
    /// a Vector4 rather than a packed color.</summary>
    private Vector4 CV(Vector4 color) => Theme.Fade(color, opacity);

    public override void Draw()
    {
        var config = plugin.Configuration;
        var nowPlaying = plugin.NowPlayingClient.Latest;
        var track = nowPlaying?.Track;
        opacity = config.BackgroundAlpha;

        lastWindowMin = ImGui.GetWindowPos();
        lastWindowMax = lastWindowMin + ImGui.GetWindowSize();
        hasWindowRect = true;

        // No native title bar left to drag by, so implement it by hand:
        // click-dragging anywhere on the window (that isn't itself an
        // active widget, e.g. the volume slider) moves it, unless locked.
        if (!config.Locked
            && ImGui.IsWindowHovered()
            && !ImGui.IsAnyItemActive()
            && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetMouseDragDelta(ImGuiMouseButton.Left));
            ImGui.ResetMouseDragDelta(ImGuiMouseButton.Left);
        }

        // The card's bottom-right extent depends on this frame's content
        // (marquee lines, error text, etc.), so draw content into channel 1
        // first, measure it, then paint the card into channel 0 behind it —
        // rather than guessing a fixed card size up front and clipping
        // whatever doesn't fit (see the cut-off play button this replaced).
        var dl = ImGui.GetWindowDrawList();
        var cardMin = ImGui.GetCursorScreenPos();
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);

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
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + WindowWidth - CardPadding * 2);
            ImGui.TextColored(CV(Theme.ErrorColor), $"Playback error: {playError}");
            ImGui.PopTextWrapPos();
        }

        ImGui.Dummy(new Vector2(1, 12));
        DrawTransportRow(config);

        ImGui.Dummy(new Vector2(1, 10));
        DrawFooterRow(config);

        ImGui.EndGroup();
        var groupMax = ImGui.GetItemRectMax();
        var cardMax = groupMax + new Vector2(CardPadding, CardPadding);

        // Zero-size marker so AlwaysAutoResize's tracked content extent
        // includes the card's bottom/right padding, not just the group.
        ImGui.SetCursorScreenPos(cardMax);
        ImGui.Dummy(Vector2.Zero);

        dl.ChannelsSetCurrent(0);
        var borderColor = clickThroughOverrideActive ? Theme.AccentSecondary : Theme.BorderColor;
        dl.AddRectFilled(cardMin, cardMax, C(Theme.BgCard), Theme.CardRounding);
        dl.AddRect(cardMin, cardMax, C(borderColor), Theme.CardRounding);
        dl.ChannelsMerge();

        DrawSettingsPopup(config);
    }

    private void DrawHeaderRow(Configuration config, NowPlaying? nowPlaying)
    {
        var badgeLabel = nowPlaying?.ListenerCount is { } count
            ? $"{count} listening"
            : nowPlaying?.Dj is not null
                ? $"Live · {nowPlaying.Dj.Name}"
                : "moogl.fm";
        var textSize = ImGui.CalcTextSize(badgeLabel);
        var badgeSize = new Vector2(textSize.X + 22, 20);
        var badgeMin = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        dl.AddRectFilled(badgeMin, badgeMin + badgeSize, C(Theme.AccentMutedBg), Theme.PillRounding);
        dl.AddRect(badgeMin, badgeMin + badgeSize, C(Theme.AccentMutedBorder), Theme.PillRounding);
        var dotCenter = badgeMin + new Vector2(11, badgeSize.Y / 2);
        dl.AddCircleFilled(dotCenter, 3f, C(Theme.Success), 12);
        dl.AddText(badgeMin + new Vector2(18, (badgeSize.Y - textSize.Y) / 2), C(Theme.AccentSecondary), badgeLabel);

        ImGui.Dummy(badgeSize);

        var iconSize = new Vector2(24, 24);
        const float iconGap = 4f;
        var iconsWidth = iconSize.X * 3 + iconGap * 2;
        // Local x is relative to the window's left edge (WindowPadding is
        // zeroed for this window), so this lines the icons up flush with
        // the card's right inset regardless of the badge's width.
        ImGui.SameLine(WindowWidth - CardPadding - iconsWidth);

        if (Widgets.IconButton("pin", iconSize, (dl2, c, s, col) => Icons.Pin(dl2, c, s, col, config.Locked), config.Locked, opacity))
        {
            config.Locked = !config.Locked;
            plugin.SaveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(config.Locked ? "Unlock window position" : "Lock window position");
        }

        ImGui.SameLine(0, iconGap);

        if (Widgets.IconButton("gear", iconSize, Icons.Gear, false, opacity))
        {
            ImGui.OpenPopup("MooglRadioSettings");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Settings");
        }

        ImGui.SameLine(0, iconGap);

        if (Widgets.IconButton("close", iconSize, Icons.Close, false, opacity))
        {
            IsOpen = false;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Close");
        }
    }

    private void DrawArt()
    {
        var texture = plugin.AlbumArtService.Texture;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        if (texture is not null)
        {
            dl.AddImageRounded(texture.Handle, pos, pos + ArtSize, Vector2.Zero, Vector2.One, C(new Vector4(1, 1, 1, 1)), 8f);
        }
        else
        {
            dl.AddRectFilled(pos, pos + ArtSize, C(Theme.TrackBg), 8f);
            dl.AddRect(pos, pos + ArtSize, C(Theme.BorderColor), 8f);
            Icons.MusicNote(dl, pos + ArtSize / 2, ArtSize.X, C(Theme.TextMuted));
        }

        // Reserve the same layout space either way so text doesn't jump
        // around when art loads in or a track has none.
        ImGui.Dummy(ArtSize);
    }

    private void DrawNowPlayingText(NowPlayingTrack? track)
    {
        var textColor = C(Theme.TextPrimary);
        var dimColor = C(Theme.TextMuted);
        var errorColor = C(Theme.ErrorColor);

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
            ImGui.TextColored(CV(Theme.TextPrimary), "Loading now-playing info...");
        }
    }

    /// <summary>
    /// Draws a single line of text clipped to <see cref="TextColumnWidth"/>,
    /// scrolling it horizontally on a loop when it's too wide to fit —
    /// avoids the wrapped/cramped look of long titles in a window that's
    /// deliberately not resizable. Text that already fits just draws
    /// normally, no motion. Takes an already-opacity-baked color (see
    /// <see cref="C"/>) since it has no config/opacity access of its own.
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
        const float buttonSize = 44f;
        const float iconSize = 22f;
        const float sliderHeight = 22f;
        const float gapAfterButton = 16f;
        const float gapAfterIcon = 8f;

        var btnMin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##playpause", new Vector2(buttonSize, buttonSize));
        var clicked = ImGui.IsItemClicked();
        var center = btnMin + new Vector2(buttonSize / 2, buttonSize / 2);
        dl.AddCircleFilled(center, buttonSize / 2, C(Theme.AccentPrimary), 32);

        var isPlaying = plugin.StreamPlayer.IsPlaying;
        var glyphColor = C(new Vector4(1, 1, 1, 1));
        if (isPlaying)
        {
            Icons.Pause(dl, center, buttonSize, glyphColor);
        }
        else
        {
            Icons.Play(dl, center, buttonSize, glyphColor);
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

        ImGui.SameLine(0, gapAfterButton);
        ImGui.BeginGroup();

        // Center the (shorter) speaker+slider row against the play button.
        var innerHeight = MathF.Max(iconSize, sliderHeight);
        var topOffset = (buttonSize - innerHeight) / 2;
        if (topOffset > 0)
        {
            ImGui.Dummy(new Vector2(1, topOffset));
        }

        var speakerPos = ImGui.GetCursorScreenPos();
        Icons.Speaker(dl, speakerPos + new Vector2(iconSize / 2, iconSize / 2), iconSize, C(Theme.TextMuted));
        ImGui.Dummy(new Vector2(iconSize, iconSize));
        ImGui.SameLine(0, gapAfterIcon);

        var sliderWidth = WindowWidth - CardPadding * 2 - buttonSize - gapAfterButton - iconSize - gapAfterIcon;
        DrawSlider("##volume", config.Volume, sliderWidth, sliderHeight, out var newVolume, Theme.AccentSecondary);
        if (newVolume != config.Volume)
        {
            config.Volume = newVolume;
            plugin.StreamPlayer.Volume = newVolume;
            plugin.SaveConfiguration();
        }

        ImGui.EndGroup();
    }

    private void DrawFooterRow(Configuration config)
    {
        ImGui.PushStyleColor(ImGuiCol.Separator, CV(Theme.BorderColor));
        ImGui.Separator();
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(1, 4));

        if (config.ClickThrough)
        {
            ImGui.TextColored(
                clickThroughOverrideActive ? CV(Theme.AccentSecondary) : CV(Theme.TextMuted),
                clickThroughOverrideActive ? "Click-through paused — release Ctrl to resume" : "Click-through on — hold Ctrl to interact");
        }
        else
        {
            ImGui.TextColored(CV(Theme.TextMuted), "MOOGL Radio · moogl.fm");
        }
    }

    /// <summary>
    /// A 0..1 slider styled like the mockup's pill sliders (flat track +
    /// accent-colored fill), driven by a plain click/drag InvisibleButton
    /// instead of ImGui's default slider visuals. Colors are faded by the
    /// current <see cref="opacity"/> (1 while the settings popup is open).
    /// </summary>
    private void DrawSlider(string id, float value, float width, float controlHeight, out float newValue, Vector4 fillColor)
    {
        var trackHeight = MathF.Min(6f, controlHeight);
        var thumbRadius = MathF.Max(6f, controlHeight * 0.36f);
        var pos = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, controlHeight);
        ImGui.PushID(id);
        ImGui.InvisibleButton("##slider", size);
        var active = ImGui.IsItemActive();
        newValue = value;

        if (active)
        {
            var rect = ImGui.GetItemRectMin();
            var pct = (ImGui.GetIO().MousePos.X - rect.X) / width;
            newValue = Math.Clamp(pct, 0f, 1f);
        }

        var dl = ImGui.GetWindowDrawList();
        var trackMin = pos + new Vector2(0, (size.Y - trackHeight) / 2);
        var trackMax = trackMin + new Vector2(width, trackHeight);
        dl.AddRectFilled(trackMin, trackMax, C(Theme.TrackBg), trackHeight / 2);
        dl.AddRectFilled(trackMin, trackMin + new Vector2(width * newValue, trackHeight), C(fillColor), trackHeight / 2);

        var thumbCenter = new Vector2(trackMin.X + width * newValue, pos.Y + size.Y / 2);
        dl.AddCircleFilled(thumbCenter, thumbRadius, C(Theme.TextPrimary), 16);
        ImGui.PopID();
    }

    private void DrawSettingsPopup(Configuration config)
    {
        // Settings must stay legible regardless of how transparent the main
        // card is, so pin opacity to 1 for the popup's lifetime and restore
        // it afterward (DrawSlider reads the same `opacity` field).
        var savedOpacity = opacity;
        opacity = 1f;

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
            DrawSlider("##volume-settings", config.Volume, 160, 16, out var newVolume, Theme.AccentPrimary);
            if (newVolume != config.Volume)
            {
                config.Volume = newVolume;
                plugin.StreamPlayer.Volume = newVolume;
                plugin.SaveConfiguration();
            }

            ImGui.SameLine();
            ImGui.TextColored(Theme.TextMuted, $"{(int)MathF.Round(config.Volume * 100)}%");

            ImGui.Dummy(new Vector2(1, 12));
            SectionHeader("Window");

            ImGui.TextColored(Theme.TextSecondary, "Opacity");
            ImGui.SameLine(90);
            DrawSlider("##opacity", config.BackgroundAlpha, 160, 16, out var newAlpha, Theme.AccentSecondary);
            if (newAlpha != config.BackgroundAlpha)
            {
                config.BackgroundAlpha = newAlpha;
                plugin.SaveConfiguration();
            }

            ImGui.SameLine();
            ImGui.TextColored(Theme.TextMuted, $"{(int)MathF.Round(config.BackgroundAlpha * 100)}%");

            ImGui.Dummy(new Vector2(1, 10));

            ToggleRow(
                "Click-through",
                "Clicks pass to the game. Hold Ctrl while hovering the window to interact anyway.",
                config.ClickThrough,
                v =>
                {
                    config.ClickThrough = v;
                    plugin.SaveConfiguration();
                });

            if (config.ClickThrough)
            {
                ImGui.TextColored(Theme.TextMuted, "Fallback: /mooglradio ct");
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

        opacity = savedOpacity;
    }

    private static void SectionHeader(string label)
    {
        ImGui.TextColored(Theme.TextMuted, label.ToUpperInvariant());
        ImGui.Dummy(new Vector2(1, 4));
    }

    private static void ToggleRow(string label, string description, bool value, Action<bool> onChange)
    {
        ImGui.PushStyleColor(ImGuiCol.Separator, Theme.BorderColor);
        ImGui.Separator();
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(1, 6));

        var cursorY = ImGui.GetCursorPosY();
        ImGui.TextColored(Theme.TextPrimary, label);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 220);
        ImGui.TextColored(Theme.TextMuted, description);
        ImGui.PopTextWrapPos();

        ImGui.SameLine(260);
        ImGui.SetCursorPosY(cursorY);
        if (Widgets.ToggleSwitch(label, value))
        {
            onChange(!value);
        }

        ImGui.Dummy(new Vector2(1, 4));
    }
}
