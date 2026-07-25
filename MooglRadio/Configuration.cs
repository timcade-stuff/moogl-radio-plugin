using Dalamud.Configuration;

namespace MooglRadio;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public float Volume { get; set; } = 0.5f;

    public string ApiBaseUrl { get; set; } = "https://moogl.fm";

    public string StreamUrl { get; set; } = "https://moogl.fm/listen/mooglradio.mp3";

    /// <summary>Pins the window in place (disables dragging).</summary>
    public bool Locked { get; set; }

    /// <summary>Makes the window ignore all mouse input, letting clicks pass through to the game.</summary>
    public bool ClickThrough { get; set; }

    /// <summary>Window background opacity, 0 (invisible) to 1 (opaque).</summary>
    public float BackgroundAlpha { get; set; } = 0.9f;
}
