using System;
using System.Numerics;
using Dalamud.Bindings.ImGui; // NOTE: if this doesn't resolve, try `using ImGuiNET;` instead —
                                // Dalamud's ImGui binding package/namespace has changed across
                                // versions and this wasn't build-verified against a real install.
using Dalamud.Interface.Windowing;
using MooglRadio.Models;

namespace MooglRadio.Windows;

/// <summary>
/// Compact, fixed-width now-playing widget. Height is remeasured from
/// content each frame and fed back into <c>SetNextWindowSize</c> the next
/// frame (see <see cref="PreDraw"/>/<see cref="lastWindowHeight"/>) so
/// layout changes can't clip content against a stale hardcoded size.
/// Visual language (dark glass card, purple/blue accent, pill toggles)
/// matches the moogl.fm site mockup, see <see cref="Theme"/> and
/// <see cref="Icons"/>.
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
    // Bumped from 14f: the header's gear/close icons and the mini player's
    // right-anchored play button sit right up against their own 24px/34px
    // hit-box edges (unlike the left side's badge/art, which has visible
    // interior padding before its content starts), so the same raw inset
    // read as noticeably tighter on the right. A larger shared inset keeps
    // both sides feeling symmetric without needing separate left/right
    // constants.
    private const float CardPadding = 18f;
    private const float TextColumnWidth = 172f;
    private const float MarqueeSpeedPxPerSecond = 40f;
    private const float MarqueeGap = 40f;

    private readonly Plugin plugin;

    private readonly VolumePopover volumePopover = new();

    private Vector2 lastWindowMin;
    private Vector2 lastWindowMax;
    private bool hasWindowRect;
    private bool clickThroughOverrideActive;
    private bool wasDragging;

    /// <summary>Last frame's measured total window height (width is always
    /// pinned to <see cref="WindowWidth"/>). Fed back into SetNextWindowSize
    /// each frame — see the comment in <see cref="PreDraw"/> for why this
    /// replaced AlwaysAutoResize + size constraints.</summary>
    private float lastWindowHeight = 220f;

    /// <summary>Current frame's opacity multiplier (== config.BackgroundAlpha),
    /// forced to 1 while the settings popup draws so it stays legible
    /// regardless of how transparent the main card is. See <see cref="C"/>.</summary>
    private float opacity = 1f;

    public MainWindow(Plugin plugin)
        : base("MOOGLradio###MooglRadioMainWindow")
    {
        this.plugin = plugin;

        // Esc is a common "close whatever's open" reflex in an MMO; a radio
        // widget shouldn't disappear because of it — only the custom X
        // button (see DrawHeaderRow) should close this window.
        RespectCloseHotkey = false;
    }

    public override void PreDraw()
    {
        var config = plugin.Configuration;

        var flags = ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoTitleBar;

        clickThroughOverrideActive = config.ClickThrough && WantsClickThroughOverride();
        if (config.ClickThrough && !clickThroughOverrideActive)
        {
            flags |= ImGuiWindowFlags.NoInputs;
        }

        // Without a title bar, ImGui's own window-move logic treats the
        // entire body as a drag handle unless NoMove is set — that native
        // move ran alongside the hand-rolled one in Draw() and ignored
        // config.Locked, since only the hand-rolled path checked it.
        if (config.Locked)
        {
            flags |= ImGuiWindowFlags.NoMove;
        }

        Flags = flags;

        // The rounded card drawn in Draw() is the only visible background —
        // it reads config.BackgroundAlpha itself (see `opacity`/C/CV). The
        // native window bg sits behind it and would otherwise show through
        // as an unrounded, unfaded square, so keep it fully transparent and
        // borderless instead.
        ImGui.SetNextWindowBgAlpha(0f);

        // AlwaysAutoResize + SetNextWindowSizeConstraints previously handled
        // sizing, but constraints are a *range*: when a frame's content (e.g.
        // a shorter badge string) measured narrower than WindowWidth, ImGui
        // picked a width below it rather than holding exactly WindowWidth —
        // and the card, still drawn assuming the full 320px, got clipped
        // square by that narrower real window edge (the reported cut-off
        // right corner). Forcing the exact size every frame removes the
        // ambiguity; height just trails last frame's measured content by
        // one frame, same lag AlwaysAutoResize had anyway.
        ImGui.SetNextWindowSize(new Vector2(WindowWidth, lastWindowHeight), ImGuiCond.Always);

        if (config.WindowPosX is { } x && config.WindowPosY is { } y)
        {
            // FirstUseEver only seeds the position once per ImGui context
            // (i.e. once per game session) — it won't fight the user's own
            // dragging on subsequent frames.
            ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.FirstUseEver);
        }

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
        var dragging = !config.Locked
            && ImGui.IsWindowHovered()
            && !ImGui.IsAnyItemActive()
            && ImGui.IsMouseDragging(ImGuiMouseButton.Left);

        if (dragging)
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetMouseDragDelta(ImGuiMouseButton.Left));
            ImGui.ResetMouseDragDelta(ImGuiMouseButton.Left);
            wasDragging = true;
        }
        else if (wasDragging)
        {
            // Persist once, on release, rather than every frame mid-drag.
            wasDragging = false;
            var pos = ImGui.GetWindowPos();
            config.WindowPosX = pos.X;
            config.WindowPosY = pos.Y;
            plugin.SaveConfiguration();
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

        if (config.MiniPlayer)
        {
            DrawMiniBody(config, nowPlaying, track);
        }
        else
        {
            DrawFullBody(config, nowPlaying, track);
        }

        ImGui.EndGroup();
        var groupMax = ImGui.GetItemRectMax();
        var cardMax = groupMax + new Vector2(CardPadding, CardPadding);

        // Belt-and-suspenders: if some future bit of content still measures
        // wider than WindowWidth despite the per-widget bounds above, clamp
        // the card rect to the window's real edge rather than letting it
        // get flat-clipped there — a flat clip silently eats the rounded
        // corner, which is exactly the bug this is guarding against.
        cardMax.X = MathF.Min(cardMax.X, cardMin.X + WindowWidth);

        // Padding=0 and no title bar means cardMin == window position exactly,
        // so this height is the exact total window height to request next
        // frame via SetNextWindowSize in PreDraw (see the comment there).
        lastWindowHeight = cardMax.Y - cardMin.Y;

        dl.ChannelsSetCurrent(0);
        var borderColor = clickThroughOverrideActive ? Theme.AccentSecondary : Theme.BorderColor;
        dl.AddRectFilled(cardMin, cardMax, C(Theme.BgCard), Theme.CardRounding);
        dl.AddRect(cardMin, cardMax, C(borderColor), Theme.CardRounding);
        dl.ChannelsMerge();

        DrawSettingsPopup(config);
    }

    private void DrawHeaderRow(Configuration config, NowPlaying? nowPlaying)
    {
        // Precedence: a live DJ set outranks the scheduled block name (DJs
        // go live mid-block), which outranks the bare "moogl.fm" fallback.
        // Listener count lives in the footer (see DrawFooterRow), not here.
        string badgeLabel;
        if (nowPlaying?.Dj is not null)
        {
            badgeLabel = $"Live · {nowPlaying.Dj.Name}";
        }
        else if (nowPlaying?.Block is { } block && !string.IsNullOrWhiteSpace(block))
        {
            badgeLabel = block;
        }
        else
        {
            badgeLabel = "moogl.fm";
        }

        var textSize = ImGui.CalcTextSize(badgeLabel);

        // A live DJ name or block title of arbitrary length shouldn't be
        // able to blow the badge past the card's right edge — that widened
        // the group's measured bounding box past WindowWidth in the same
        // way the old unbounded Separator did (see DrawDivider), and the
        // excess got clipped square by the window edge, losing the card's
        // right-side rounding entirely.
        var maxBadgeWidth = WindowWidth - CardPadding * 2 - (24 * 4 + 4 * 3 + 8);
        var badgeSize = new Vector2(MathF.Min(textSize.X + 22, maxBadgeWidth), 20);
        var badgeMin = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        dl.AddRectFilled(badgeMin, badgeMin + badgeSize, C(Theme.AccentMutedBg), Theme.PillRounding);
        dl.AddRect(badgeMin, badgeMin + badgeSize, C(Theme.AccentMutedBorder), Theme.PillRounding);
        var dotCenter = badgeMin + new Vector2(11, badgeSize.Y / 2);
        dl.AddCircleFilled(dotCenter, 3f, C(Theme.Success), 12);
        dl.PushClipRect(badgeMin, badgeMin + badgeSize, true);
        dl.AddText(badgeMin + new Vector2(18, (badgeSize.Y - textSize.Y) / 2), C(Theme.AccentSecondary), badgeLabel);
        dl.PopClipRect();

        ImGui.Dummy(badgeSize);

        var iconSize = new Vector2(24, 24);
        const float iconGap = 4f;
        var iconsWidth = iconSize.X * 4 + iconGap * 3;
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

        if (Widgets.IconButton("mini-player", iconSize, Icons.MiniPlayerToggle, config.MiniPlayer, opacity))
        {
            config.MiniPlayer = !config.MiniPlayer;
            plugin.SaveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(config.MiniPlayer ? "Switch to full player" : "Switch to mini player");
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

    /// <summary>
    /// A horizontal rule bounded to exactly WindowWidth - CardPadding*2,
    /// unlike ImGui.Separator() (used safely in the auto-sized settings
    /// popup, but NOT here): Separator spans the window's full work rect,
    /// which — since WindowPadding is zeroed for full-bleed card drawing —
    /// is the entire window width, not our CardPadding-inset content
    /// width. That widened this group's measured bounding box past the
    /// window's forced size, and the excess (plus the corner rounding)
    /// got clipped square by the window edge — the reported cut-off,
    /// unrounded right side.
    /// </summary>
    private void DrawDivider()
    {
        var pos = ImGui.GetCursorScreenPos();
        var width = WindowWidth - CardPadding * 2;
        ImGui.GetWindowDrawList().AddLine(pos, pos + new Vector2(width, 0), C(Theme.BorderColor), 1f);
        ImGui.Dummy(new Vector2(width, 1));
    }

    private void DrawArt() => DrawArt(ArtSize);

    private void DrawArt(Vector2 size)
    {
        var texture = plugin.AlbumArtService.Texture;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        if (texture is not null)
        {
            dl.AddImageRounded(texture.Handle, pos, pos + size, Vector2.Zero, Vector2.One, C(new Vector4(1, 1, 1, 1)), 8f);
        }
        else
        {
            dl.AddRectFilled(pos, pos + size, C(Theme.TrackBg), 8f);
            dl.AddRect(pos, pos + size, C(Theme.BorderColor), 8f);
            Icons.MusicNote(dl, pos + size / 2, size.X, C(Theme.TextMuted));
        }

        // Reserve the same layout space either way so text doesn't jump
        // around when art loads in or a track has none.
        ImGui.Dummy(size);
    }

    private void DrawNowPlayingText(NowPlaying? nowPlaying, NowPlayingTrack? track)
    {
        var textColor = C(Theme.TextPrimary);
        var dimColor = C(Theme.TextMuted);
        var errorColor = C(Theme.ErrorColor);

        if (track is not null)
        {
            // No leading Dummy here — the title's top must line up exactly
            // with the album art's top (both start at their group's cursor
            // position with zero offset).
            DrawMarquee(track.Title, textColor, TextColumnWidth);
            DrawMarquee(track.Artist, dimColor, TextColumnWidth);

            // Album gets its own marquee line below the artist rather than
            // sharing one via "Artist · Album" — long combined strings made
            // the marquee scroll through both at once, which was harder to
            // read than two independently-scrolling lines.
            if (!string.IsNullOrWhiteSpace(track.Album))
            {
                DrawMarquee(track.Album, dimColor, TextColumnWidth);
            }

            // Null (no fixed track length, e.g. a live DJ set) is skipped
            // rather than shown as "LIVE" — the header badge already says
            // that (see DrawHeaderRow) and repeating it here is just noise.
            if (nowPlaying?.Dj is null && plugin.NowPlayingClient.GetRemainingSeconds() is { } remaining)
            {
                ImGui.TextColored(CV(Theme.TextMuted), $"{remaining / 60}:{remaining % 60:D2} remaining");
            }
        }
        else if (plugin.NowPlayingClient.LastError is { } metaError)
        {
            DrawMarquee($"Can't reach MOOGLradio ({metaError})", errorColor, TextColumnWidth);
        }
        else
        {
            ImGui.TextColored(CV(Theme.TextPrimary), "Loading now-playing info...");
        }
    }

    /// <summary>
    /// Draws a single line of text clipped to <paramref name="width"/>,
    /// scrolling it horizontally on a loop when it's too wide to fit —
    /// avoids the wrapped/cramped look of long titles in a window that's
    /// deliberately not resizable. Text that already fits just draws
    /// normally, no motion. Takes an already-opacity-baked color (see
    /// <see cref="C"/>) since it has no config/opacity access of its own.
    /// </summary>
    private static void DrawMarquee(string text, uint colorU32, float width)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var lineHeight = ImGui.GetTextLineHeight();
        var textSize = ImGui.CalcTextSize(text);

        ImGui.Dummy(new Vector2(width, lineHeight));

        if (textSize.X <= width)
        {
            drawList.AddText(pos, colorU32, text);
            return;
        }

        drawList.PushClipRect(pos, pos + new Vector2(width, lineHeight), true);
        var cycle = textSize.X + MarqueeGap;
        var offset = (float)(ImGui.GetTime() * MarqueeSpeedPxPerSecond % cycle);
        drawList.AddText(pos - new Vector2(offset, 0), colorU32, text);
        drawList.AddText(pos + new Vector2(cycle - offset, 0), colorU32, text);
        drawList.PopClipRect();
    }

    private void DrawFullBody(Configuration config, NowPlaying? nowPlaying, NowPlayingTrack? track)
    {
        ImGui.BeginGroup();
        DrawArt();
        ImGui.EndGroup();
        ImGui.SameLine();
        ImGui.BeginGroup();
        DrawNowPlayingText(nowPlaying, track);
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
        DrawFooterRow(config, nowPlaying);
    }

    private void DrawTransportRow(Configuration config)
    {
        var dl = ImGui.GetWindowDrawList();
        const float buttonSize = 44f;
        const float progressHeight = 6f;
        const float volumeButtonSize = 30f;
        const float gapAfterButton = 16f;
        const float gapBeforeVolume = 8f;

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

        // Progress bar is thinner than the play button, so it's centered
        // within a row as tall as the button rather than sitting flush
        // with its top.
        var progressTopOffset = (buttonSize - progressHeight) / 2;
        if (progressTopOffset > 0)
        {
            ImGui.Dummy(new Vector2(1, progressTopOffset));
        }

        var progressWidth = WindowWidth - CardPadding * 2 - buttonSize - gapAfterButton - volumeButtonSize - gapBeforeVolume;
        Widgets.ProgressBar(progressWidth, progressHeight, plugin.NowPlayingClient.GetProgress(), Theme.TrackBg, Theme.AccentSecondary, opacity);

        ImGui.EndGroup();
        ImGui.SameLine(0, gapBeforeVolume);
        ImGui.BeginGroup();

        var volumeTopOffset = (buttonSize - volumeButtonSize) / 2;
        if (volumeTopOffset > 0)
        {
            ImGui.Dummy(new Vector2(1, volumeTopOffset));
        }

        var newVolume = volumePopover.Draw(volumeButtonSize, config.Volume, opacity);
        if (newVolume != config.Volume)
        {
            config.Volume = newVolume;
            plugin.StreamPlayer.Volume = newVolume;
            plugin.SaveConfiguration();
        }

        ImGui.EndGroup();
    }

    private void DrawFooterRow(Configuration config, NowPlaying? nowPlaying)
    {
        DrawDivider();
        ImGui.Dummy(new Vector2(1, 4));

        // Listener count sits right-aligned on the same row as the
        // branding/click-through text, mirroring the header's live dot —
        // reserve its width up front so the left text wraps before
        // colliding with it instead of running underneath.
        var listenerText = nowPlaying?.ListenerCount is { } count ? $"{count} listening" : null;
        var listenerWidth = listenerText is not null ? ImGui.CalcTextSize(listenerText).X : 0f;
        var listenerReserve = listenerText is not null ? listenerWidth + 12f : 0f;
        var cursorY = ImGui.GetCursorPosY();

        // Wrapped for the same reason as the playback-error text above: an
        // unbounded line here can measure wider than the card, pushing the
        // group's bounding box past WindowWidth and flat-clipping the
        // card's right-side rounding.
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + WindowWidth - CardPadding * 2 - listenerReserve);
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
        ImGui.PopTextWrapPos();

        if (listenerText is not null)
        {
            ImGui.SetCursorPos(new Vector2(WindowWidth - CardPadding - listenerWidth, cursorY));
            ImGui.TextColored(CV(Theme.TextMuted), listenerText);
        }
    }

    /// <summary>
    /// Compact single-row layout: art, track text and the play/pause
    /// button share one row, a progress bar sits below it, and listener
    /// count / time remaining / volume live in a footer row — mirrors the
    /// mini player mockup rather than the full body's stacked art+text
    /// then separate transport/footer rows.
    /// </summary>
    private void DrawMiniBody(Configuration config, NowPlaying? nowPlaying, NowPlayingTrack? track)
    {
        const float artSize = 44f;
        const float playButtonSize = 34f;
        const float gapArtText = 10f;
        const float gapTextPlay = 10f;
        var contentWidth = WindowWidth - CardPadding * 2;

        var dl = ImGui.GetWindowDrawList();
        var rowTop = ImGui.GetCursorScreenPos();

        ImGui.BeginGroup();
        DrawArt(new Vector2(artSize, artSize));
        ImGui.EndGroup();

        ImGui.SameLine(0, gapArtText);
        var textWidth = contentWidth - artSize - gapArtText - playButtonSize - gapTextPlay;
        ImGui.BeginGroup();
        DrawMiniTrackText(track, textWidth);
        ImGui.EndGroup();

        ImGui.SameLine(WindowWidth - CardPadding - playButtonSize);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (artSize - playButtonSize) / 2);

        var btnMin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##mini-playpause", new Vector2(playButtonSize, playButtonSize));
        var clicked = ImGui.IsItemClicked();
        var center = btnMin + new Vector2(playButtonSize / 2, playButtonSize / 2);
        dl.AddCircleFilled(center, playButtonSize / 2, C(Theme.AccentPrimary), 32);

        var isPlaying = plugin.StreamPlayer.IsPlaying;
        var glyphColor = C(new Vector4(1, 1, 1, 1));
        if (isPlaying)
        {
            Icons.Pause(dl, center, playButtonSize, glyphColor);
        }
        else
        {
            Icons.Play(dl, center, playButtonSize, glyphColor);
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

        // Art is the tallest element in the row (44px vs. the 34px play
        // button); the row's real bottom is the art's, so realign the
        // cursor there rather than trusting wherever the play button
        // (offset downward to center it) left it.
        ImGui.SetCursorScreenPos(rowTop + new Vector2(0, artSize));

        if (plugin.StreamPlayer.LastError is { } playError)
        {
            ImGui.Dummy(new Vector2(1, 4));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + contentWidth);
            ImGui.TextColored(CV(Theme.ErrorColor), $"Playback error: {playError}");
            ImGui.PopTextWrapPos();
        }

        ImGui.Dummy(new Vector2(1, 12));
        Widgets.ProgressBar(contentWidth, 4f, plugin.NowPlayingClient.GetProgress(), Theme.TrackBg, Theme.AccentSecondary, opacity);

        ImGui.Dummy(new Vector2(1, 8));
        DrawMiniFooterRow(config, nowPlaying);
    }

    private void DrawMiniTrackText(NowPlayingTrack? track, float width)
    {
        var textColor = C(Theme.TextPrimary);
        var dimColor = C(Theme.TextMuted);
        var errorColor = C(Theme.ErrorColor);

        if (track is not null)
        {
            DrawMarquee(track.Title, textColor, width);
            DrawMarquee(track.Artist, dimColor, width);

            if (!string.IsNullOrWhiteSpace(track.Album))
            {
                DrawMarquee(track.Album, dimColor, width);
            }
        }
        else if (plugin.NowPlayingClient.LastError is { } metaError)
        {
            DrawMarquee($"Can't reach MOOGLradio ({metaError})", errorColor, width);
        }
        else
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextColored(CV(Theme.TextPrimary), "Loading...");
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawMiniFooterRow(Configuration config, NowPlaying? nowPlaying)
    {
        const float iconSize = 14f;
        const float volumeButtonSize = 26f;
        var dl = ImGui.GetWindowDrawList();
        var rowTop = ImGui.GetCursorScreenPos();
        var contentWidth = WindowWidth - CardPadding * 2;

        // "listening" is dropped here (unlike the full player's footer) —
        // the headset icon already conveys that, and the mini player is
        // meant to be as compact as possible.
        var listenerText = nowPlaying?.ListenerCount is { } count ? $"{count:N0}" : null;
        if (listenerText is not null)
        {
            var iconCenter = rowTop + new Vector2(iconSize / 2, iconSize / 2);
            Icons.Headset(dl, iconCenter, iconSize, C(Theme.TextMuted));
            ImGui.Dummy(new Vector2(iconSize, iconSize));
            ImGui.SameLine(0, 5);
            ImGui.TextColored(CV(Theme.TextMuted), listenerText);
        }
        else
        {
            ImGui.Dummy(new Vector2(1, iconSize));
        }

        // Live DJ sets have no fixed track length — RemainingSeconds and
        // DurationSeconds both go null together (see NowPlaying doc
        // comments), so there's nothing to count down. The header badge
        // already flags a live DJ; this just says LIVE rather than showing
        // a blank where the countdown would be.
        string? timeLabel = null;
        if (nowPlaying?.Dj is not null)
        {
            timeLabel = "LIVE";
        }
        else if (plugin.NowPlayingClient.GetRemainingSeconds() is { } remaining)
        {
            timeLabel = $"-{remaining / 60}:{remaining % 60:D2}";
        }

        if (timeLabel is not null)
        {
            var timeWidth = ImGui.CalcTextSize(timeLabel).X;
            ImGui.SetCursorScreenPos(rowTop + new Vector2((contentWidth - timeWidth) / 2, 0));
            ImGui.TextColored(CV(Theme.TextMuted), timeLabel);
        }

        // The button itself is an ImGui item, so it extends this group's
        // measured bounds (used to size the card, see Draw()) on its own —
        // no follow-up Dummy needed just to reserve its height.
        ImGui.SetCursorScreenPos(rowTop + new Vector2(contentWidth - volumeButtonSize, (iconSize - volumeButtonSize) / 2));
        var newVolume = volumePopover.Draw(volumeButtonSize, config.Volume, opacity);
        if (newVolume != config.Volume)
        {
            config.Volume = newVolume;
            plugin.StreamPlayer.Volume = newVolume;
            plugin.SaveConfiguration();
        }
    }

    /// <summary>
    /// A 0..1 slider styled like the mockup's pill sliders (flat track +
    /// accent-colored fill), driven by a plain click/drag InvisibleButton
    /// instead of ImGui's default slider visuals. Colors are faded by the
    /// current <see cref="opacity"/> (1 while the settings popup is open).
    /// The track is inset by <c>thumbRadius</c> on each side so the thumb
    /// travels entirely within <paramref name="width"/> — without the inset,
    /// a value near 1.0 pushed the thumb's circle past the reserved width,
    /// eating into the caller's right-hand padding (e.g. the transport row
    /// sizing its slider to end flush with the card's right inset).
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

        var trackWidth = width - thumbRadius * 2;

        if (active)
        {
            var rect = ImGui.GetItemRectMin();
            var pct = (ImGui.GetIO().MousePos.X - rect.X - thumbRadius) / trackWidth;
            newValue = Math.Clamp(pct, 0f, 1f);
        }

        var dl = ImGui.GetWindowDrawList();
        var trackMin = pos + new Vector2(thumbRadius, (size.Y - trackHeight) / 2);
        var trackMax = trackMin + new Vector2(trackWidth, trackHeight);
        dl.AddRectFilled(trackMin, trackMax, C(Theme.TrackBg), trackHeight / 2);
        dl.AddRectFilled(trackMin, trackMin + new Vector2(trackWidth * newValue, trackHeight), C(fillColor), trackHeight / 2);

        var thumbCenter = new Vector2(trackMin.X + trackWidth * newValue, pos.Y + size.Y / 2);
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
                "Mini player",
                "Compact single-row layout. Also toggleable from the header icon or /mooglradio mini.",
                config.MiniPlayer,
                v =>
                {
                    config.MiniPlayer = v;
                    plugin.SaveConfiguration();
                });

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

            ImGui.Dummy(new Vector2(1, 6));
            SectionHeader("Chat notifications");

            ToggleRow(
                "Track change",
                "Prints the new track's title, artist, and album to chat",
                config.ChatNotifyTrackChange,
                v =>
                {
                    config.ChatNotifyTrackChange = v;
                    plugin.SaveConfiguration();
                });

            ToggleRow(
                "Block change",
                "Prints to chat when a programming block starts or ends",
                config.ChatNotifyBlockChange,
                v =>
                {
                    config.ChatNotifyBlockChange = v;
                    plugin.SaveConfiguration();
                });

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
