# MooglRadio (Dalamud plugin)

In-game player for [MOOGL Radio](https://moogl.fm), a compact ImGui window
docked over the game. No DJ/admin functionality here; that lives on the
website.

## Features

- **Playback** — play/pause the stream, volume slider (`Services/StreamPlayer.cs`).
- **Now-playing info** — cover art and scrolling title/artist/album text,
  live DJ badge, current programming block, listener count, remaining-time
  countdown (`Services/NowPlayingClient.cs`, `Services/AlbumArtService.cs`,
  `Windows/MainWindow.cs`).
- **Window controls** — adjustable background opacity, pin/lock (disables
  dragging), click-through (clicks pass to the game; hold Ctrl to interact
  anyway), and a compact single-row mini player, all via the gear icon or
  chat subcommands (`Windows/MainWindow.cs`).
- **Game BGM auto-mute** — mutes the game's own background music while the
  radio plays and restores it on stop, via Dalamud's official `IGameConfig`
  (not memory hacking), on by default (`Services/BgmMuter.cs`).
- **Chat notifications** — optional chat lines on track change and on
  programming block start/end, both off by default (`Plugin.cs`).
- **Listener location map (opt-in, off by default)** — shares your current
  zone and in-game position with moogl.fm every 30 seconds while the
  stream plays, powering the site's live listener map. Anonymous
  per-session ID only, generated in memory and never persisted; no
  character name, account, or server is ever sent
  (`Services/ListenerLocationClient.cs`). See the Mitigations & Security
  Considerations section below for the full privacy contract.

## Commands

- `/mooglradio` — toggle the player window.
- `/mooglradio lock` / `/mooglradio unlock` — pin/unpin the window position.
- `/mooglradio ct` — toggle click-through. Since a click-through window
  can't be clicked to reach its own gear-icon settings, this chat command
  is the way back out if you get stuck in click-through mode.

## Status: core playback confirmed working in-game as of 2026-07-25

This was scaffolded and developed in an environment with no local
Dalamud/XIVLauncher install, so everything below was iterated on blind
and confirmed (or fixed) against real in-game reports:

- ✅ `Dalamud.NET.Sdk/15.0.0` resolves, restores, and the csproj is
  structurally valid (confirmed via `dotnet build`).
- ✅ Target framework is `net10.0-windows` (set by the SDK itself, don't
  override it).
- ✅ Compiles against real Dalamud/ImGui/FFXIVClientStructs assemblies —
  verified in CI (`.github/workflows/release.yml`, downloads Dalamud's
  published assemblies from `goatcorp.github.io/dalamud-distrib`).
- ✅ `DalamudPackager` packages a working `MooglRadio.zip` and CI publishes
  it to GitHub Releases automatically on tag push (see `v0.1.0`).
- ✅ **Confirmed in-game (v0.1.10):** stream connects, decodes, and plays
  audibly; game BGM correctly mutes while playing. See item 3 below for
  the full chain of issues this took.
- ✅ Cover art (`AlbumArtService`) and the scrolling marquee text are
  confirmed working in-game — see items 7-8 below.
- ❌ Instant-stop-on-pause (item 6 below) is **not** fixed — audible lag
  after pausing is still reproducible in-game despite the volume-zeroing
  change.

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
3. **MP3 decoding/playback under Wine** — ✅ confirmed working in-game as
   of v0.1.10 (2026-07-25), after several issues fixed in a row, all
   confirmed in-game along the way: (a)
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
   premature-completion flakiness on some .NET runtimes — **did not fix
   it**: confirmed in-game "Connected: HTTP/1.1" logged correctly, still
   ended at frame 4. Real cause, found by reading NAudio's own official
   streaming demo (`NAudioDemo/Mp3StreamingDemo`): it wraps the network
   stream in `ReadFullyStream`, which our from-scratch
   `PositionTrackingStream` never did. `Stream.Read()` is only guaranteed
   to return *at least one* byte when not at EOF, not the full count
   requested — network streams routinely return short reads, and
   `Mp3Frame.LoadFromStream` doesn't loop on its own reads, so a short
   read gets silently misread as a truncated frame (matching the
   nondeterministic 3-6 frame cutoff — real network packet timing, not a
   fixed count). Fixed by giving `PositionTrackingStream` the same
   read-ahead-buffer full-read loop as NAudio's `ReadFullyStream`. **This
   fixed it** — confirmed in-game, audio actually plays.
4. **`DalamudPackager` target** — enabled and confirmed working via CI
   (produces `MooglRadio.zip`), but only compile-verified, not run
   in-game.
5. **`BgmMuter` (`Services/BgmMuter.cs`)** — ✅ confirmed working in-game:
   mutes the game's BGM via `IGameConfig.Set(SystemConfigOption.IsSndBgm,
   true)` while the radio plays, restoring the previous value on stop.
   The `true` = muted direction was inferred (not just the option names,
   which came straight from Dalamud's source) and has now been confirmed
   correct in-game — no inversion needed.
6. **Instant stop on Pause (`StreamPlayer.StopInternal`)** — ❌ still
   broken, re-confirmed in-game: pressing Pause still keeps playing
   buffered audio for a while before actually going silent, even with the
   volume-zeroing fix (zeroing `WaveFloatTo16Provider.Volume` before
   calling `waveOut.Stop()`/`Dispose()`) in place. The BGM does correctly
   un-mute immediately (confirming `Stop()` itself fires right away), so
   the remaining lag is specifically in already-queued audio at the
   device/driver level. Likely genuinely inside Wine's `WaveOutEvent`
   buffering and not something app-level code can fully control without a
   different output backend — worth revisiting if this needs a real fix.
7. **`AlbumArtService` (`Services/AlbumArtService.cs`)** — ✅ confirmed
   working in-game. Downloads the current track's cover art
   (`{ApiBaseUrl}{track.ArtUrl}`) and converts it via
   `ITextureProvider.CreateFromImageAsync` for rendering with
   `ImGui.Image`; renders correctly, including the larger hover preview
   added later.
8. **Scrolling marquee text (`MainWindow.DrawMarquee`)** — ✅ confirmed
   working in-game. Hand-rolled via `ImGuiWindowDrawList.PushClipRect` +
   `AddText` with a time-based scroll offset (`ImGui.GetTime()`), since
   ImGui has no built-in marquee widget; scrolls smoothly and stays
   legible for long titles, with the two-copy wraparound looping
   seamlessly.

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
2. Search "MOOGL Radio" in the plugin installer and install it. Updates
   after that are automatic — CI publishes a new release (and this
   repo.json always points at `releases/latest`) whenever a `v*` tag is
   pushed.
3. `/mooglradio` toggles the player window; see the Commands section
   above for lock/click-through subcommands.

For local dev iteration instead, use "Load DevPlugin" pointed at the
built DLL.

## Releasing a new version

Three files carry the version number and **all three must match** before
tagging, or the plugin will fail to install/update with a version
mismatch (this bit us once — `MooglRadio.json` got bumped but
`repo.json`/the csproj didn't, silently shipping a broken release):

1. `MooglRadio/MooglRadio.csproj` — `<Version>` (this is what actually
   gets baked into the built assembly)
2. `MooglRadio/MooglRadio.json` — `AssemblyVersion` (packaged into the
   release zip's manifest)
3. `repo.json` — `AssemblyVersion` (what the in-game installer checks
   against to decide an update is available; lives at repo root, served
   raw from `main`)

All three should be the identical `x.y.z.0` value. Bump all three in the
same commit, then tag and push:

```bash
git tag vX.Y.Z && git push origin vX.Y.Z
```

Pushing the tag triggers `.github/workflows/release.yml`, which builds
and publishes `MooglRadio.zip` to a GitHub Release — `repo.json`'s
`DownloadLink*` fields always point at `releases/latest`, so no other
repo.json changes are needed per release beyond the version bump above.

## Submitting to the official Dalamud plugin repo

In progress — see `submission/manifest.toml` for the draft manifest
that goes into a PR against
[DalamudPluginsD17](https://github.com/goatcorp/DalamudPluginsD17)
(`testing/live/MooglRadio/`). Still needed before that PR can go up:

- `submission/images/icon.png` (square, 64-512px) — art by Ciera Killo
- Update the `commit` field in the manifest to whatever commit is
  actually being submitted (it's a snapshot, not auto-updating).
- Disclose AI tool usage in the PR description per the
  [AI usage policy](https://dalamud.dev/plugin-publishing/ai-policy/) —
  significant portions of this plugin were built with AI assistance
  (Claude), under human direction and in-game testing throughout (see
  the confirmation log above). Say so plainly in the PR.

## License

[MIT](LICENSE)

## AI Disclosure
This project is built in a copilot mode - the LLM does most of the coding, with human interaction at every step of the journey. Testing is all done by a human, with automations for builds/releases and security scans built as GHA pipelines within the repository.

### Mitigations & Security Considerations
In an attempt to mitigate security concerns that are always at the heart of any project built with LLMs, I've included a variety of code scans to try to ensure the integrity of the project as much as possible. Most data is collected from public endpoints via GET; the one exception is described below. See the `SECURITY_AUDIT.md` file for my findings and the remediations taken therein.

**Listener location (opt-in, off by default):** when enabled in Settings, the plugin POSTs an anonymous heartbeat (a random per-session ID generated in memory, your current zone, and in-game position) to moogl.fm every 30 seconds while the stream is playing, powering the site's "where are listeners tuning in from" map. The session ID is never persisted to disk and is never derived from your character name, account, or server — none of that is ever sent. Disabling the setting or stopping playback stops the heartbeats (no explicit disconnect call is sent; the server simply expires the session after a short timeout).