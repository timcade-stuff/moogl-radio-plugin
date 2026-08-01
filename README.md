# MooglRadio (Dalamud plugin)

In-game player for [MOOGLradio](https://moogl.fm)
— play/pause, volume, cover art, and scrolling now-playing track/DJ info,
in a compact, fixed-size ImGui window. Background opacity is adjustable,
and the window can be pinned (no drag) and set click-through via the gear
icon or chat subcommands. While the radio plays, the game's own BGM is
muted (via Dalamud's official `IGameConfig`, not memory hacking) so it
doesn't layer under the stream — toggle this in the gear-icon settings,
on by default. No DJ/admin functionality here; that lives on the website.

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
- ❌ Not yet confirmed in-game: cover art (`AlbumArtService`), the
  scrolling marquee text, and the instant-stop-on-pause fix — all added
  after the v0.1.10 confirmation, see items 6-8 below.

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
6. **Instant stop on Pause (`StreamPlayer.StopInternal`)** — not yet
   confirmed in-game. Reported: pressing Pause kept playing buffered
   audio for a while before actually going silent, though the BGM
   correctly un-muted immediately (confirming `Stop()` itself does fire
   right away). Suspected cause: under Wine, `WaveOutEvent.Stop()`
   doesn't always flush already-queued device buffers instantly. Fixed
   by zeroing `WaveFloatTo16Provider.Volume` before calling
   `waveOut.Stop()`/`Dispose()`, so anything still in flight at the
   device level comes out silent rather than audible. If this doesn't
   fully fix it, the remaining gap is genuinely inside Wine's audio
   driver and not something app-level code can control.
7. **`AlbumArtService` (`Services/AlbumArtService.cs`)** — not yet
   confirmed in-game. Downloads the current track's cover art
   (`{ApiBaseUrl}{track.ArtUrl}`) and converts it via
   `ITextureProvider.CreateFromImageAsync` for rendering with
   `ImGui.Image`. Two specific unknowns: (a) whether
   `CreateFromImageAsync` is actually safe to call off the main/framework
   thread — its doc comment doesn't flag a main-thread requirement
   (unlike `CreateTextureFromSeString`, which explicitly does), but this
   hasn't been exercised in-game; (b) the exact `ImGui.Image` overload
   signature in `Dalamud.Bindings.ImGui` — used the 2-arg
   `(ImTextureID, Vector2)` form, may need `uv0`/`uv1`/tint/border
   params depending on the binding version.
8. **Scrolling marquee text (`MainWindow.DrawMarquee`)** — not yet
   confirmed in-game. Hand-rolled via `ImGuiWindowDrawList.PushClipRect`
   + `AddText` with a time-based scroll offset (`ImGui.GetTime()`), since
   ImGui has no built-in marquee widget. Text that fits the column just
   draws normally; only overflowing lines scroll. Two-copy wraparound
   (`AddText` called twice per scrolling line) should make the loop
   seamless, but the exact spacing/legibility hasn't been seen rendered.

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

### Mitigations
In an attempt to mitigate security concerns that are always at the heart of any project built with LLMs, I've included a variety of code scans to try to ensure the integrity of the project as much as possible. Since data is collected from public endpoints via GET and no data is sent to the server using POST, the attack surface is limited. See the `SECURITY_AUDIT.md` file for my findings and the remediations taken therein.

NOTE: There are plans for minimal data-sending that will only be done when an option is enabled in the configuration of the plugin, where it will share *where* in Eorzea you're listening from while playing. This feature will only pull data of the current player's location and post it to the site anonymously, so it can be displayed on a map. This will *only* work when enabled.