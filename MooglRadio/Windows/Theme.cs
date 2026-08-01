using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MooglRadio.Windows;

/// <summary>
/// Color/shape language lifted from the moogl.fm site mockup (dark glass
/// card, purple-to-blue accent, pill toggles) so the in-game widget reads
/// as the same product. ImGui can't do backdrop blur or CSS gradients, so
/// these are flattened to solid colors that read the same at a glance.
/// </summary>
internal static class Theme
{
    public static readonly Vector4 BgCard = new(0.086f, 0.067f, 0.106f, 1f);
    public static readonly Vector4 BorderColor = new(1f, 1f, 1f, 0.10f);

    public static readonly Vector4 TextPrimary = new(0.93f, 0.92f, 0.97f, 1f);
    public static readonly Vector4 TextSecondary = new(0.78f, 0.76f, 0.86f, 1f);
    public static readonly Vector4 TextMuted = new(0.55f, 0.53f, 0.63f, 1f);
    public static readonly Vector4 ErrorColor = new(1f, 0.5f, 0.5f, 1f);

    public static readonly Vector4 AccentPrimary = new(0.486f, 0.227f, 0.929f, 1f); // #7C3AED
    public static readonly Vector4 AccentSecondary = new(0.243f, 0.678f, 0.976f, 1f); // #3EADF9
    public static readonly Vector4 AccentMutedBg = new(0.486f, 0.227f, 0.929f, 0.16f);
    public static readonly Vector4 AccentMutedBorder = new(0.486f, 0.227f, 0.929f, 0.35f);

    public static readonly Vector4 Success = new(0.133f, 0.773f, 0.369f, 1f); // #22C55E
    public static readonly Vector4 TrackBg = new(1f, 1f, 1f, 0.08f);
    public static readonly Vector4 HoverBg = new(1f, 1f, 1f, 0.08f);

    public const float CardRounding = 10f;
    public const float PillRounding = 999f;

    public static uint U32(Vector4 color) => ImGui.GetColorU32(color);

    /// <summary>Scales a color's alpha by <paramref name="opacity"/> — used to make the
    /// whole hand-drawn widget fade with the window's opacity setting, since raw
    /// ImDrawList colors don't participate in ImGui's own style alpha.</summary>
    public static Vector4 Fade(Vector4 color, float opacity) => new(color.X, color.Y, color.Z, color.W * opacity);
}

/// <summary>
/// Small hand-drawn vector icons (pin, gear, play/pause, speaker, close)
/// so the widget doesn't depend on an icon font being present. Everything
/// is drawn centered in a square of <paramref name="size"/> px.
/// </summary>
internal static class Icons
{
    public static void Pin(ImDrawListPtr dl, Vector2 center, float size, uint color, bool pinned)
    {
        var r = size * 0.24f;
        var headCenter = center + new Vector2(0, -size * 0.12f);

        if (!pinned)
        {
            // Unpinned: tilt the whole glyph like the mockup's rotated pin.
            const float angle = -0.7f;
            Vector2 Rot(Vector2 p) => center + Rotate(p - center, angle);
            headCenter = Rot(headCenter);
            dl.AddCircleFilled(headCenter, r, color, 16);
            var tip = Rot(center + new Vector2(0, size * 0.42f));
            var baseL = Rot(headCenter + new Vector2(-r * 0.55f, r * 0.55f));
            var baseR = Rot(headCenter + new Vector2(r * 0.55f, r * 0.55f));
            dl.AddTriangleFilled(baseL, baseR, tip, color);
            return;
        }

        dl.AddCircleFilled(headCenter, r, color, 16);
        var tipP = center + new Vector2(0, size * 0.42f);
        var baseLP = headCenter + new Vector2(-r * 0.55f, r * 0.55f);
        var baseRP = headCenter + new Vector2(r * 0.55f, r * 0.55f);
        dl.AddTriangleFilled(baseLP, baseRP, tipP, color);
    }

    private static Vector2 Rotate(Vector2 v, float angle)
    {
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        return new Vector2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    public static void Gear(ImDrawListPtr dl, Vector2 center, float size, uint color)
    {
        var outerR = size * 0.42f;
        var innerR = outerR * 0.55f;
        var toothR = outerR * 1.22f;
        const int teeth = 8;

        for (var i = 0; i < teeth; i++)
        {
            var a = i * (MathF.PI * 2f / teeth);
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            var perp = new Vector2(-dir.Y, dir.X) * (outerR * 0.28f);
            var baseP = center + dir * outerR * 0.85f;
            var tipP = center + dir * toothR;
            dl.AddTriangleFilled(baseP + perp, baseP - perp, tipP, color);
        }

        dl.AddCircleFilled(center, outerR, color, 24);
        dl.AddCircleFilled(center, innerR, Theme.U32(Theme.BgCard), 20);
    }

    public static void Close(ImDrawListPtr dl, Vector2 center, float size, uint color)
    {
        var r = size * 0.28f;
        const float thickness = 1.6f;
        dl.AddLine(center + new Vector2(-r, -r), center + new Vector2(r, r), color, thickness);
        dl.AddLine(center + new Vector2(-r, r), center + new Vector2(r, -r), color, thickness);
    }

    public static void Play(ImDrawListPtr dl, Vector2 center, float size, uint color)
    {
        var r = size * 0.32f;
        var p1 = center + new Vector2(-r * 0.7f, -r);
        var p2 = center + new Vector2(-r * 0.7f, r);
        var p3 = center + new Vector2(r * 0.9f, 0);
        dl.AddTriangleFilled(p1, p2, p3, color);
    }

    public static void Pause(ImDrawListPtr dl, Vector2 center, float size, uint color)
    {
        var barW = size * 0.16f;
        var barH = size * 0.62f;
        var gap = size * 0.14f;
        var top = center.Y - barH / 2;
        dl.AddRectFilled(new Vector2(center.X - gap - barW, top), new Vector2(center.X - gap, top + barH), color, 1.5f);
        dl.AddRectFilled(new Vector2(center.X + gap, top), new Vector2(center.X + gap + barW, top + barH), color, 1.5f);
    }

    public static void Speaker(ImDrawListPtr dl, Vector2 center, float size, uint color)
    {
        var w = size * 0.28f;
        var h = size * 0.32f;
        var boxLeft = center.X - w * 0.9f;
        var coneTip = center.X + w * 0.35f;

        dl.AddRectFilled(new Vector2(boxLeft, center.Y - h * 0.35f), new Vector2(center.X - w * 0.2f, center.Y + h * 0.35f), color, 1.5f);
        dl.AddTriangleFilled(
            new Vector2(center.X - w * 0.2f, center.Y - h * 0.35f),
            new Vector2(center.X - w * 0.2f, center.Y + h * 0.35f),
            new Vector2(coneTip, center.Y - h),
            color);
        dl.AddTriangleFilled(
            new Vector2(center.X - w * 0.2f, center.Y - h * 0.35f),
            new Vector2(coneTip, center.Y - h),
            new Vector2(coneTip, center.Y + h),
            color);
        dl.AddTriangleFilled(
            new Vector2(center.X - w * 0.2f, center.Y + h * 0.35f),
            new Vector2(coneTip, center.Y + h),
            new Vector2(center.X - w * 0.2f, center.Y - h * 0.35f),
            color);

        // Sound-wave arcs.
        dl.PathArcTo(center + new Vector2(w * 0.1f, 0), size * 0.30f, -0.6f, 0.6f, 10);
        dl.PathStroke(color, ImDrawFlags.None, 1.4f);
        dl.PathArcTo(center + new Vector2(w * 0.1f, 0), size * 0.44f, -0.5f, 0.5f, 10);
        dl.PathStroke(color, ImDrawFlags.None, 1.4f);
    }

    /// <summary>Picture-in-picture glyph (outer frame + inset rect) used for the
    /// mini-player toggle — a small rect "docked" in a larger one reads as
    /// switching to a compact view without needing separate on/off icons.</summary>
    public static void MiniPlayerToggle(ImDrawListPtr dl, Vector2 center, float size, uint color)
    {
        var outerW = size * 0.62f;
        var outerH = size * 0.46f;
        var outerMin = center - new Vector2(outerW / 2, outerH / 2);
        var outerMax = center + new Vector2(outerW / 2, outerH / 2);
        dl.AddRect(outerMin, outerMax, color, 2f, ImDrawFlags.None, 1.4f);

        var innerW = size * 0.26f;
        var innerH = size * 0.2f;
        var innerMax = outerMax - new Vector2(3f, 3f);
        var innerMin = innerMax - new Vector2(innerW, innerH);
        dl.AddRectFilled(innerMin, innerMax, color, 1.5f);
    }

    /// <summary>Headset glyph for the mini player's listener count, matching
    /// the mockup's headphone icon (arc "band" over two ear-cup rects).</summary>
    public static void Headset(ImDrawListPtr dl, Vector2 center, float size, uint color)
    {
        var r = size * 0.34f;
        const float thickness = 1.6f;
        dl.PathArcTo(center + new Vector2(0, size * 0.06f), r, MathF.PI, MathF.PI * 2, 16);
        dl.PathStroke(color, ImDrawFlags.None, thickness);

        var cupSize = new Vector2(size * 0.16f, size * 0.24f);
        var cupY = center.Y + size * 0.06f;
        dl.AddRectFilled(
            new Vector2(center.X - r - cupSize.X * 0.3f, cupY),
            new Vector2(center.X - r - cupSize.X * 0.3f + cupSize.X, cupY + cupSize.Y),
            color, 2f);
        dl.AddRectFilled(
            new Vector2(center.X + r - cupSize.X * 0.7f, cupY),
            new Vector2(center.X + r - cupSize.X * 0.7f + cupSize.X, cupY + cupSize.Y),
            color, 2f);
    }

    public static void MusicNote(ImDrawListPtr dl, Vector2 center, float size, uint color)
    {
        var r = size * 0.11f;
        var stemH = size * 0.42f;
        var noteL = center + new Vector2(-size * 0.14f, size * 0.14f);
        var noteR = center + new Vector2(size * 0.14f, size * 0.10f);
        dl.AddCircleFilled(noteL, r, color, 12);
        dl.AddCircleFilled(noteR, r, color, 12);
        dl.AddLine(noteL + new Vector2(r - 1, 0), noteL + new Vector2(r - 1, -stemH), color, 1.6f);
        dl.AddLine(noteR + new Vector2(r - 1, 0), noteR + new Vector2(r - 1, -stemH), color, 1.6f);
        dl.AddLine(noteL + new Vector2(r - 1, -stemH), noteR + new Vector2(r - 1, -stemH), color, 1.6f);
    }
}

/// <summary>
/// Small interactive controls in the mockup's style that plain ImGui
/// doesn't provide out of the box (pill icon buttons, iOS-style switches).
/// </summary>
internal static class Widgets
{
    public static bool IconButton(string id, Vector2 size, Action<ImDrawListPtr, Vector2, float, uint> drawIcon, bool active = false, float opacity = 1f)
    {
        ImGui.PushID(id);
        ImGui.InvisibleButton("##btn", size);
        var clicked = ImGui.IsItemClicked();
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var center = (min + max) / 2;
        var dl = ImGui.GetWindowDrawList();

        if (active)
        {
            dl.AddRectFilled(min, max, Theme.U32(Theme.Fade(Theme.AccentMutedBg, opacity)), 6f);
        }
        else if (hovered)
        {
            dl.AddRectFilled(min, max, Theme.U32(Theme.Fade(Theme.HoverBg, opacity)), 6f);
        }

        var iconColor = Theme.U32(Theme.Fade(active ? Theme.AccentSecondary : Theme.TextSecondary, opacity));
        drawIcon(dl, center, MathF.Min(size.X, size.Y), iconColor);
        ImGui.PopID();
        return clicked;
    }

    /// <summary>
    /// Thin rounded track + fill bar for the current track's playback
    /// progress. Purely decorative (no drag/click handling) — progress is
    /// derived client-side from the now-playing poll, not something the
    /// user can seek. <paramref name="progress"/> null means "unknown"
    /// (live DJ set, or a file whose duration couldn't be read) and draws
    /// an empty track rather than guessing a fill amount.
    /// </summary>
    public static void ProgressBar(float width, float height, float? progress, Vector4 trackColor, Vector4 fillColor, float opacity)
    {
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var rounding = height / 2;

        dl.AddRectFilled(pos, pos + new Vector2(width, height), Theme.U32(Theme.Fade(trackColor, opacity)), rounding);

        if (progress is { } pct)
        {
            var fillWidth = width * Math.Clamp(pct, 0f, 1f);
            if (fillWidth > 0)
            {
                dl.AddRectFilled(pos, pos + new Vector2(fillWidth, height), Theme.U32(Theme.Fade(fillColor, opacity)), rounding);
            }
        }

        ImGui.Dummy(new Vector2(width, height));
    }

    public static bool ToggleSwitch(string id, bool value)
    {
        var size = new Vector2(36, 20);
        ImGui.PushID(id);
        ImGui.InvisibleButton("##toggle", size);
        var clicked = ImGui.IsItemClicked();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();

        var trackColor = value ? Theme.U32(Theme.AccentPrimary) : Theme.U32(Theme.TrackBg);
        dl.AddRectFilled(min, max, trackColor, size.Y / 2);

        var thumbR = size.Y / 2 - 2;
        var thumbX = value ? max.X - thumbR - 2 : min.X + thumbR + 2;
        var thumbCenter = new Vector2(thumbX, (min.Y + max.Y) / 2);
        dl.AddCircleFilled(thumbCenter, thumbR, Theme.U32(new Vector4(1f, 1f, 1f, 1f)), 16);

        ImGui.PopID();
        return clicked;
    }
}
