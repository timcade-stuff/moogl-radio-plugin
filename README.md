# MooglRadio (Dalamud plugin)

In-game player for [MOOGLradio](https://github.com/REPLACE_ME/moogl-radio)
— play/pause, volume, and now-playing track/DJ info, in a compact,
fixed-size ImGui window. Background opacity is adjustable, and the window
can be pinned (no drag) and set click-through via the gear icon or chat
subcommands. While the radio plays, the game's own BGM is muted (via
Dalamud's official `IGameConfig`, not memory hacking) so it doesn't layer
under the stream — toggle this in the gear-icon settings, on by default.
No DJ/admin functionality here; that lives on the website.

## Commands

- `/mooglradio` — toggle the player window.
- `/mooglradio lock` / `/mooglradio unlock` — pin/unpin the window position.
- `/mooglradio ct` — toggle click-through. Since a click-through window
  can't be clicked to reach its own gear-icon settings, this chat command
  is the way back out if you get stuck in click-through mode.

## Status: builds and packages cleanly in CI, not yet run in-game

This was scaffolded and developed in an environment with no local
Dalamud/XIVLauncher install:

- ✅ `Dalamud.NET.Sdk/15.0.0` resolves, restores, and the csproj is
  structurally valid (confirmed via `dotnet build`).
- ✅ Target framework is `net10.0-windows` (set by the SDK itself, don't
  override it).
- ✅ Compiles against real Dalamud/ImGui/FFXIVClientStructs assemblies —
  verified in CI (`.github/workflows/release.yml`, downloads Dalamud's
  published assemblies from `goatcorp.github.io/dalamud-distrib`).
- ✅ `DalamudPackager` packages a working `MooglRadio.zip` and CI publishes
  it to GitHub Releases automatically on tag push (see `v0.1.0`).
- ❌ Not run in-game. Playback (NAudio/ACM mp3 decoding) and the ImGui
  binding namespace/glyph rendering are unverified — CI proves it
  compiles, not that it works at runtime.

Things to double-check when you pick this up for real:

1. **`Dalamud.Bindings.ImGui` namespace** — `Windows/MainWindow.cs`
   assumes `using Dalamud.Bindings.ImGui;` gives you the `ImGui` static
   class. If that doesn't resolve, try `using ImGuiNET;` instead —
   Dalamud's ImGui binding has been renamed across versions. Also unverified:
   `Window.PreDraw()`/`Flags`/`ImGui.SetNextWindowBgAlpha` (used for the
   pin/click-through/opacity controls) and the ⏸/▶/⚙ glyphs rendering with
   Dalamud's default ImGui font — swap for text labels if they show as
   tofu boxes.
2. **`DalamudApiLevel` in `MooglRadio/MooglRadio.json` / `repo.json`** —
   was a placeholder `13`; updated to `15` on 2026-07-25 after checking
   the live PluginMaster feed (Dalamud silently hides plugins whose
   declared API level doesn't match current from the installer list,
   with no error shown — this is why the plugin wasn't appearing
   in-game). Re-check this value whenever Dalamud ships a new API level
   and the plugin needs a corresponding release.
3. **MP3 decoding/playback under Wine** — still being chased as of
   2026-07-25, several issues fixed in a row, all confirmed in-game: (a)
   `AcmMp3FrameDecompressor` calling into the (Wine-less) Windows ACM
   codec, fixed by swapping to `NLayer.NAudioSupport.Mp3FrameDecompressor`
   (pure C# decode); (b) NAudio's `Mp3Frame.LoadFromStream` unconditionally
   reads `input.Position` as its first line, which throws on the
   non-seekable HTTP response stream — fixed with
   `StreamPlayer.PositionTrackingStream`; (c) NLayer's decoder outputs
   32-bit IEEE float, and `WaveOutEvent` (winmm) accepted `Init()`/`Play()`
   on that format without erroring while producing no audible output at
   all — fixed by converting to 16-bit PCM via NAudio's
   `WaveFloatTo16Provider`; (d) even after that conversion, `WaveOutEvent`
   still produced no sound (no exception either) — tried swapping to
   `WasapiOut`, **reverted**: research into
   [CrystalRadio](https://github.com/Saevath/CrystalRadio), a real
   published Dalamud radio plugin, found it uses plain `WaveOutEvent`
   successfully, real-world evidence against the WASAPI theory. (CrystalRadio
   also uses `MediaFoundationReader` instead of manual MP3 parsing — not
   adopted here, since Wine/Proton's Media Foundation support is
   inconsistent (see e.g. `mf-fix` projects), which would reintroduce the
   platform risk NLayer's pure-C# decode was specifically chosen to avoid.)
   `StreamPlayer.Diagnostic` now logs periodic per-frame buffering progress
   (not just start/end checkpoints) via `IPluginLog.Info` — check `/xllog`
   for "MOOGLradio:" lines. That surfaced the next issue: the stream was
   disconnecting after only 3-6 frames (~100ms of audio), confirmed via
   the "Stream ended after N frames" diagnostic, even though the same URL
   streams fine via `curl`. Likely cause: `HttpClient` negotiates HTTP/2
   by default against Cloudflare, and HTTP/2 combined with
   `Mp3Frame.LoadFromStream`'s synchronous `Stream.Read()` calls has known
   premature-completion flakiness on some .NET runtimes — fixed by
   pinning the request to HTTP/1.1 (`HttpVersionPolicy.RequestVersionExact`).
   Not yet confirmed in-game as of this writing.
4. **`DalamudPackager` target** — enabled and confirmed working via CI
   (produces `MooglRadio.zip`), but only compile-verified, not run
   in-game.
5. **`BgmMuter` (`Services/BgmMuter.cs`)** — mutes the game's BGM via
   `IGameConfig.Set(SystemConfigOption.IsSndBgm, true)` while the radio
   plays, restoring the previous value on stop. The option names
   (`IsSndBgm`, `SoundBgm`) are confirmed from Dalamud's own source, but
   the specific "`true` = muted" semantics are inferred (it maps to the
   "Music" mute checkbox in Character Configuration → Sound) and not yet
   confirmed in-game — if muting the radio's own `Play` button instead
   mutes the wrong direction (unmutes BGM, or fails silently), the fix
   is inverting the bool in `BgmMuter.MuteForRadio`/`RestoreGameBgm`.

## Building

Requires:

- [XIVLauncher](https://goatcorp.github.io/) installed, with the game
  launched at least once through it so Dalamud installs itself
  (typically to `%appdata%\XIVLauncher\addon\Hooks\dev\` on Windows,
  `~/.xlcore/dalamud/Hooks/dev/` on Linux, or
  `~/Library/Application Support/XIV on Mac/dalamud/Hooks/dev/` on
  Mac — override with the `DALAMUD_HOME` env var if yours lives
  elsewhere).
- .NET SDK matching `net10.0-windows` (see `MooglRadio/MooglRadio.csproj`).
- On macOS/Linux, cross-compiling `net10.0-windows` needs the Windows
  desktop reference assemblies; if `dotnet build` fails with
  `NETSDK1073` about `Microsoft.WindowsDesktop.App.WindowsForms`, you're
  missing that reference pack. Building via Windows (native, a VM, or a
  `windows-latest` CI runner) sidesteps this entirely and is the more
  common path for Dalamud plugin dev.

```bash
dotnet build MooglRadio/MooglRadio.csproj
```

## Installing (custom plugin repo)

1. In-game or in the Dalamud plugin installer, go to
   **Settings → Experimental → Custom Plugin Repositories** and add:
   ```
   https://raw.githubusercontent.com/timcade-stuff/moogl-radio-plugin/main/repo.json
   ```
2. Search "MOOGLradio" in the plugin installer and install it. Updates
   after that are automatic — CI publishes a new release (and this
   repo.json always points at `releases/latest`) whenever a `v*` tag is
   pushed.
3. `/mooglradio` toggles the player window; see the Commands section
   above for lock/click-through subcommands.

For local dev iteration instead, use "Load DevPlugin" pointed at the
built DLL.

## Eventual goal

Submit to the official Dalamud plugin repo, which has its own
submission process/guidelines (manifest requirements, review). Treat
that as a post-v1 milestone, not a blocker for getting this working for
yourself first.
