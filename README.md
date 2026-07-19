# MooglRadio (Dalamud plugin)

In-game player for [MOOGLradio](https://github.com/REPLACE_ME/moogl-radio)
— play/pause, volume, and now-playing track/DJ info, docked in an ImGui
window. No DJ/admin functionality here; that lives on the website.

## Status: early scaffold, not yet build-verified in-game

This was scaffolded and probe-built in an environment with no local
Dalamud/XIVLauncher install, so it's been validated as far as tooling
allows and no further:

- ✅ `Dalamud.NET.Sdk/15.0.0` resolves, restores, and the csproj is
  structurally valid (confirmed via `dotnet build`).
- ✅ Target framework is `net10.0-windows` (set by the SDK itself, don't
  override it).
- ❌ Not compiled against real Dalamud/ImGui/FFXIVClientStructs
  assemblies — that requires an actual Dalamud install (see below).
- ❌ Not run in-game. Playback (NAudio/ACM mp3 decoding), the ImGui
  binding namespace, and the DalamudPackager packaging step are all
  unverified.

Things to double-check when you pick this up for real:

1. **`Dalamud.Bindings.ImGui` namespace** — `Windows/MainWindow.cs`
   assumes `using Dalamud.Bindings.ImGui;` gives you the `ImGui` static
   class. If that doesn't resolve, try `using ImGuiNET;` instead —
   Dalamud's ImGui binding has been renamed across versions.
2. **`DalamudApiLevel` in `MooglRadio/MooglRadio.json` / `repo.json`** —
   currently set to `13` as a placeholder; check Dalamud's current API
   level (visible in-game under Dalamud Settings → About, or in the
   Dalamud repo) and correct it.
3. **MP3 decoding under Wine** — `Services/StreamPlayer.cs` uses
   NAudio's `AcmMp3FrameDecompressor`, which relies on the Windows ACM
   codec. FFXIV runs under Wine on Mac/Linux (confirmed via the
   `DalamudLibPath` for macOS pointing at `.../XIV on Mac/dalamud/...`),
   and Wine prefixes don't always have an mp3 ACM codec registered. If
   playback silently fails, swap in the pure-C# decoder from the
   `NLayer.NAudioSupport` NuGet package instead (drop-in replacement,
   same constructor shape).
4. **`DalamudPackager` target** — left commented out in
   `MooglRadio.csproj` pending confirmation of the current task
   parameters against a real build. Uncomment and adjust once you can
   build for real.
5. **`RepoUrl` / download links** — `MooglRadio/MooglRadio.json` and
   `repo.json` have `REPLACE_ME` placeholders; fill in once this has a
   real GitHub repo and releases.

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

## Testing in-game

1. In Dalamud Settings → Experimental, add this repo's `repo.json` raw
   URL as a custom plugin repo (once published), or use "Load DevPlugin"
   pointed at the built DLL for local iteration.
2. `/mooglradio` toggles the player window.

## Eventual goal

Submit to the official Dalamud plugin repo, which has its own
submission process/guidelines (manifest requirements, review). Treat
that as a post-v1 milestone, not a blocker for getting this working for
yourself first.
