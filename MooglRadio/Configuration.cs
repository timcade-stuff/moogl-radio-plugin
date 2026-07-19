using Dalamud.Configuration;

namespace MooglRadio;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public float Volume { get; set; } = 0.5f;

    public string ApiBaseUrl { get; set; } = "https://radio.moogl.ing";

    public string StreamUrl { get; set; } = "https://radio.moogl.ing/listen/mooglradio.mp3";
}
