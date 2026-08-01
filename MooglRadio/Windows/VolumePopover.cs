using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MooglRadio.Windows;

/// <summary>
/// Speaker icon button that opens a small popover with a vertical volume
/// fader, matching the mockup's volume control. Shared by both the full
/// player's transport row and the mini player's footer row — each just
/// calls <see cref="Draw"/> at its own cursor position rather than each
/// having its own copy of the button/popover/slider logic.
///
/// One instance is reused across both layouts (only one layout is ever
/// visible per frame), so open/close state doesn't need to be duplicated
/// per layout.
/// </summary>
internal sealed class VolumePopover
{
    private const string PopupId = "MooglRadioVolumePopover";
    private bool isOpen;

    /// <summary>Draws the button at the current cursor position and, while
    /// open, the popover above it. Returns the possibly-updated volume —
    /// callers compare against their current value to decide whether to
    /// persist/apply it, same pattern as <see cref="MainWindow"/>'s
    /// DrawSlider.</summary>
    public float Draw(float buttonSize, float volume, float opacity)
    {
        var clicked = Widgets.IconButton(
            "volume-toggle", new Vector2(buttonSize, buttonSize),
            (dl, c, s, col) => Icons.Speaker(dl, c, s, col), isOpen, opacity);
        var btnMin = ImGui.GetItemRectMin();
        var btnMax = ImGui.GetItemRectMax();

        if (ImGui.IsItemHovered() && !isOpen)
        {
            ImGui.SetTooltip("Volume");
        }

        if (clicked)
        {
            isOpen = !isOpen;
            if (isOpen)
            {
                ImGui.OpenPopup(PopupId);
            }
        }

        if (!isOpen)
        {
            return volume;
        }

        const float popupWidth = 46f;
        // Tall enough for the label line + gap + 74px fader + top/bottom
        // WindowPadding (10*2) with room to spare — the old 110f was a hair
        // short of the actual measured content, which made ImGui treat the
        // popup as overflowing and draw a scrollbar over the fader.
        const float popupHeight = 130f;
        const float gap = 6f;

        // Anchored above the button, right edge flush with it — same
        // corner the mockup's popover hangs off (bottom:34px;right:0 of
        // the button's own relative container).
        var popupPos = new Vector2(btnMax.X - popupWidth, btnMin.Y - popupHeight - gap);
        ImGui.SetNextWindowPos(popupPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(popupWidth, popupHeight));

        ImGui.PushStyleColor(ImGuiCol.PopupBg, Theme.BgCard);
        // A visibly brighter border than the main card's (Theme.BorderColor
        // is a subtle 10%-alpha hairline meant to sit against the card's own
        // opaque background) — this popover floats directly over game world
        // content, so it needs a clearer edge to read as a distinct surface.
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1f, 1f, 1f, 0.35f));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1.5f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 10));

        var newVolume = volume;
        if (ImGui.BeginPopup(PopupId, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var label = $"{(int)MathF.Round(volume * 100)}%";
            var labelWidth = ImGui.CalcTextSize(label).X;
            var contentWidth = ImGui.GetContentRegionAvail().X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (contentWidth - labelWidth) / 2));
            ImGui.TextColored(Theme.TextMuted, label);

            ImGui.Dummy(new Vector2(1, 4));
            newVolume = DrawVerticalSlider("##volume-fader", volume, contentWidth, 74f, Theme.AccentPrimary);

            ImGui.EndPopup();
        }
        else
        {
            // ImGui already closed the popup itself (click outside, Esc) —
            // mirror that into isOpen so the button stops rendering active.
            isOpen = false;
        }

        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);

        return newVolume;
    }

    /// <summary>Vertical counterpart to <see cref="MainWindow"/>'s horizontal
    /// pill slider — bottom = 0, top = 1, like a physical fader. Fill grows
    /// upward from the bottom as the mockup's vertical range input does.</summary>
    private static float DrawVerticalSlider(string id, float value, float containerWidth, float height, Vector4 fillColor)
    {
        const float trackWidth = 6f;
        var thumbRadius = MathF.Max(6f, trackWidth * 1.4f);

        var pos = ImGui.GetCursorScreenPos();
        ImGui.PushID(id);
        ImGui.InvisibleButton("##vslider", new Vector2(containerWidth, height));
        var active = ImGui.IsItemActive();
        var newValue = value;

        var trackHeight = height - thumbRadius * 2;
        if (active && trackHeight > 0)
        {
            var rect = ImGui.GetItemRectMin();
            var pct = 1f - (ImGui.GetIO().MousePos.Y - rect.Y - thumbRadius) / trackHeight;
            newValue = Math.Clamp(pct, 0f, 1f);
        }

        var dl = ImGui.GetWindowDrawList();
        var centerX = pos.X + containerWidth / 2;
        var trackMin = new Vector2(centerX - trackWidth / 2, pos.Y + thumbRadius);
        var trackMax = new Vector2(centerX + trackWidth / 2, trackMin.Y + trackHeight);
        dl.AddRectFilled(trackMin, trackMax, Theme.U32(Theme.TrackBg), trackWidth / 2);

        var fillTop = trackMax.Y - trackHeight * newValue;
        dl.AddRectFilled(new Vector2(trackMin.X, fillTop), trackMax, Theme.U32(fillColor), trackWidth / 2);

        var thumbCenter = new Vector2(centerX, fillTop);
        dl.AddCircleFilled(thumbCenter, thumbRadius, Theme.U32(Theme.TextPrimary), 16);
        ImGui.PopID();

        return newValue;
    }
}
