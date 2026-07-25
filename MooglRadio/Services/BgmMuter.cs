using Dalamud.Game.Config;
using Dalamud.Plugin.Services;

namespace MooglRadio.Services;

/// <summary>
/// Mutes the game's own background music while the radio plays, so you
/// don't hear zone/combat BGM layered under the stream, and restores
/// whatever it was set to once the radio stops. Uses Dalamud's official
/// IGameConfig service to toggle IsSndBgm — the internal name of the
/// "Music" mute checkbox in Character Configuration > Sound (independent
/// of the volume slider, and independent of sound effects/voice/ambient,
/// which are untouched). No memory hooking or signature scanning.
///
/// Unverified against a real Dalamud install (see README): specifically,
/// whether IsSndBgm's semantics really are "true = muted" as assumed here
/// (inferred from it mapping directly to that in-game checkbox) rather
/// than the inverse.
/// </summary>
public sealed class BgmMuter(IGameConfig gameConfig, IPluginLog log)
{
    private bool? previouslyMuted;

    public void MuteForRadio()
    {
        if (previouslyMuted is not null)
        {
            // Already muted by us from an earlier Play() — don't clobber
            // the "previous" state with our own change.
            return;
        }

        try
        {
            previouslyMuted = gameConfig.TryGet(SystemConfigOption.IsSndBgm, out bool current) && current;
            gameConfig.Set(SystemConfigOption.IsSndBgm, true);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "MOOGLradio: failed to mute game BGM");
        }
    }

    public void RestoreGameBgm()
    {
        if (previouslyMuted is not { } wasMuted)
        {
            return;
        }

        previouslyMuted = null;

        try
        {
            gameConfig.Set(SystemConfigOption.IsSndBgm, wasMuted);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "MOOGLradio: failed to restore game BGM mute state");
        }
    }
}
