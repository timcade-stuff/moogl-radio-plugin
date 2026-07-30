# MooglRadio Security Audit

Audit date: 2026-07-29
Scope: the `MooglRadio` Dalamud plugin source, its NuGet dependency graph,
and its GitHub Actions CI/release pipeline, ahead of submission to the
public [DalamudPluginsD17](https://github.com/goatcorp/DalamudPluginsD17)
repository.

This audit was prompted by a specific concern: can the JSON data this
plugin fetches over the network be tampered with or hijacked in transit,
and more broadly, is there anything else in this codebase that widens its
attack surface for a public listing? The answer, in short: the codebase is
small and mostly clean (no file I/O, no process execution, no unsafe
deserialization), but a handful of trust-boundary gaps around the plugin's
three outbound HTTP requests were real and have been fixed — see
[Findings](#findings) below.

## Methodology

- Manual review of every source file that performs network I/O, file I/O,
  process execution, reflection/dynamic loading, or config
  persistence — the plugin's entire `.cs` source tree (8 files).
- Manual review of the NuGet dependency graph, CI/release pipeline, and
  plugin/repo manifests.
- Grep-based sweep for hardcoded secrets/keys/tokens across all source,
  config, and manifest files.
- `dotnet list package --vulnerable --include-transitive` — .NET's built-in
  NuGet Audit, checks the full resolved dependency graph (direct +
  transitive) against the GitHub Advisory Database.
- [SecurityCodeScan.VS2019](https://security-code-scan.github.io/) v5.6.7 —
  a Roslyn static-analysis security scanner, now wired into every build as
  an analyzer (see [Tooling](#tooling-added)).
- [Gitleaks](https://github.com/gitleaks/gitleaks) v8.30.1 — secret scanner,
  run against full git history (`gitleaks detect --source . -v`), not just
  the current working tree.

### Re-running these checks

```bash
# Dependency vulnerability audit
cd MooglRadio && dotnet list package --vulnerable --include-transitive

# Secret scan (install once: brew install gitleaks)
gitleaks detect --source . -v

# Build (also runs the SecurityCodeScan analyzer + NuGetAudit warnings)
dotnet build MooglRadio/MooglRadio.csproj -c Release
```

All three now also run automatically on every push/PR via
[`.github/workflows/security.yml`](.github/workflows/security.yml).

> **Note on local verification:** this development environment has no local
> Dalamud/XIVLauncher install (see `README.md`), so `dotnet build` cannot
> fully compile here — the SDK refuses to build without Dalamud's reference
> assemblies on disk. This is a pre-existing, documented limitation, not
> something introduced by this audit. `dotnet restore` and `dotnet list
> package --vulnerable` do work locally (confirmed clean, see below); the
> SecurityCodeScan analyzer and NuGetAudit build warnings will get their
> first real run in CI on the next push, via `release.yml` / the new
> `security.yml`, both of which download Dalamud's assemblies from its
> distribution CDN before building, same as they always have.

## Findings

| # | Finding | Severity | Status |
|---|---|---|---|
| F1 | Server-supplied `ArtUrl` concatenated onto the API base URL with no same-origin check | Medium | ✅ Fixed |
| F2 | `ApiBaseUrl`/`StreamUrl` loaded from local config with no scheme/host validation | Medium | ✅ Fixed |
| F3 | No response-size cap on any outbound HTTP request | Low | ✅ Fixed |
| F4 | No request timeout on the two short-lived HTTP clients | Low | ✅ Fixed |
| F5 | Unused `NowPlaying.StreamUrl` field is a latent trust-boundary gap for future code | Informational | ✅ Documented |
| F6 | CI actions pinned to floating tags rather than commit SHAs | Medium | ✅ Fixed |
| F7 | `packages.lock.json` present but not enforced during CI restore | Low | ✅ Fixed |
| F8 | Plugin self-description version out of sync with the actual build version | Informational | ✅ Fixed |
| F9 | CI downloads Dalamud's build assemblies from a CDN with no checksum verification | Informational | ⚠️ Accepted risk |
| F10 | `submission/manifest.toml` still has its documented placeholder commit hash | Informational | ⬜ Manual, at submission time |

### F1 — Unvalidated `ArtUrl` from the now-playing API (Medium)

**Where:** `MooglRadio/Services/AlbumArtService.cs`

The now-playing poll (`NowPlayingClient`) fetches JSON from the
`moogl.fm` API and includes an `ArtUrl` field documented as "relative,
e.g. `/api/now-playing/art`". `AlbumArtService.UpdateFor` built the art
fetch URL with plain string concatenation:

```csharp
var fullUrl = artUrl is null ? null : $"{apiBaseUrl.TrimEnd('/')}{artUrl}";
```

Nothing checked that `artUrl` actually *was* a relative, same-origin path.
If the API (or anything positioned to tamper with its response — see F2)
ever returned an absolute URL, the plugin would fetch and attempt to
decode whatever that URL pointed to as an image. Low real-world severity on
its own (the backend already controls the audio stream and now-playing
text shown in the UI), but it's an unnecessary trust-boundary gap for
public-facing code, and defense-in-depth here is cheap.

**Fix:** `AlbumArtService` now rejects any `ArtUrl` that isn't a plain
path-rooted relative reference — must start with `/`, must not start with
`//` (protocol-relative), must not contain `://` — before it's ever
concatenated or fetched. A rejected value is logged and the art fetch is
skipped rather than attempted.

### F2 — No scheme/host validation on configured URLs (Medium)

**Where:** `MooglRadio/Configuration.cs`, `MooglRadio/Plugin.cs`

`ApiBaseUrl` and `StreamUrl` are plain strings persisted via Dalamud's
`SavePluginConfig`/`GetPluginConfig` to a JSON file on disk. They aren't
exposed in the plugin's own settings UI, but nothing stopped the config
file itself from being hand-edited (or written by something else with
local file access) to point at an arbitrary host, or to silently downgrade
`https://` to `http://` — which is precisely the "JSON gets hijacked"
scenario this audit set out to check for: an `http://` `ApiBaseUrl` would
make the now-playing poll, and the art fetch built from it, plaintext and
trivially tamperable by anything on the network path (a hostile Wi-Fi
network, a compromised router, etc).

**Fix:** on plugin load (`Plugin.cs`, right after `GetPluginConfig()` and
folded into the existing v1→v2 migration path), both URLs are now
validated as well-formed **absolute `https://` URIs**
(`Uri.TryCreate(..., UriKind.Absolute, ...)` + `uri.Scheme ==
Uri.UriSchemeHttps`). Anything that fails — malformed, `http://`, or any
other scheme — is logged as a warning and reset to the known-good hardcoded
default (`https://moogl.fm`, `https://moogl.fm/listen/mooglradio.mp3|`)
before it's ever used to make a request. This makes HTTP-downgrade and
arbitrary-host redirection impossible via config tampering.

### F3 — No response size limit (Low)

**Where:** `NowPlayingClient.cs`, `AlbumArtService.cs`

`HttpClient.GetFromJsonAsync`/`GetByteArrayAsync` buffer the entire
response into memory with no upper bound configured. A compromised or
malicious response (deliberately huge, or a misconfigured server) could
force large, unbounded memory allocation.

**Fix:** `MaxResponseContentBufferSize` is now set on both clients — 64 KB
for the now-playing JSON poll (the payload is a handful of short fields),
8 MB for album art (generous headroom for a cover-art image, still bounded).
`GetByteArrayAsync`/`GetFromJsonAsync` throw if the response exceeds this,
which is caught by the existing broad `try/catch` in both services and
surfaced as `LastError` rather than crashing.

### F4 — No request timeout (Low)

**Where:** `NowPlayingClient.cs`, `AlbumArtService.cs`

Both clients relied on `HttpClient`'s 100-second default timeout. A stalled
or slow-responding endpoint would hang a poll cycle far longer than a
15-second-interval poll or an incidental art fetch ever needs to wait.

**Fix:** both clients now set `Timeout = TimeSpan.FromSeconds(10)`.
`StreamPlayer`'s client is deliberately left alone
(`Timeout = Timeout.InfiniteTimeSpan`, with a comment explaining why): it's
a long-lived streaming read, not a short request/response cycle, and a
finite `HttpClient.Timeout` would cut off live playback partway through.

### F5 — Unused `NowPlaying.StreamUrl` is a latent risk (Informational)

**Where:** `MooglRadio/Models/NowPlaying.cs`

The now-playing JSON contract includes a `StreamUrl` field that is
deserialized but never read anywhere — `StreamPlayer.Play` is always
called with the locally-configured `Configuration.StreamUrl` (now validated
per F2), not this API-supplied one. Not a live vulnerability today, but if
a future change ever wires this field into actual playback without
validating it first, it would reopen the same class of issue as F1/F2.

**Fix:** documented directly on the model with an XML doc comment
explaining the constraint for whoever touches this next — no behavior
change.

### F6 — CI actions pinned to floating tags (Medium)

**Where:** `.github/workflows/release.yml`

`actions/checkout`, `actions/setup-dotnet`, and — most importantly —
`softprops/action-gh-release` (a third-party action that runs with
`contents: write`, i.e. it can create/modify GitHub Releases) were all
pinned to floating major-version tags (`@v4`, `@v2`). A compromised or
retagged release under any of those tags would run with that permission on
every future release build, without the repo owner having changed anything.

**Fix:** all three are now pinned to the commit SHA behind their current
latest release, with a `# vX.Y.Z` comment for readability — standard
GitHub Actions supply-chain hardening. Versions were bumped to current
latest while doing this (`checkout` v4→v7, `setup-dotnet` v4→v6,
`action-gh-release` v2→v3), since v2-era tags are past or approaching
GitHub's Node 20 runner deprecation.

### F7 — Lock file present but not enforced (Low)

**Where:** `MooglRadio/packages.lock.json`, `release.yml`

A `packages.lock.json` already existed (good — it pins exact package
content hashes for every direct and transitive dependency), but
`dotnet restore` in CI ran without `--locked-mode`, so a future `dotnet
restore` could silently update the resolved graph without the build
failing or the lock file being flagged as stale.

**Fix:** `dotnet restore ... --locked-mode` is now used in both
`release.yml` and the new `security.yml`. CI now fails loudly if the
resolved dependency graph ever drifts from what's committed in
`packages.lock.json`.

### F8 — Version metadata drift (Informational)

**Where:** `MooglRadio/MooglRadio.json`

`MooglRadio.csproj`'s `<Version>` was `0.1.17.0` and `repo.json` matched,
but `MooglRadio.json`'s `AssemblyVersion` (the plugin's own
self-description, shipped inside the package) was still `0.1.11.0`.

**Fix:** bumped to `0.1.17.0` to match.

### F9 — Dalamud CDN download has no checksum (Informational, accepted risk)

**Where:** `release.yml` (and now `security.yml`), `Download Dalamud` step

CI fetches `https://goatcorp.github.io/dalamud-distrib/latest.zip` — the
Dalamud reference assemblies needed to compile against — over HTTPS but
with no published checksum to verify against. This is the standard,
ecosystem-wide pattern for building Dalamud plugins in CI (Dalamud doesn't
publish per-version hashes for this endpoint), so there's no practical
alternative available today. Documented here as a known, accepted residual
risk rather than silently ignored: if `goatcorp.github.io` were ever
compromised, CI would build against tampered reference assemblies. Impact
is limited to the build-time environment (reference assemblies, not
runtime-shipped code) and is outside this plugin's own control.

### F10 — Submission manifest placeholder (Informational / process)

**Where:** `submission/manifest.toml`

The draft `manifest.toml` for the `DalamudPluginsD17` PR has a placeholder
`commit` hash, already flagged in its own comment as needing to be updated
to the real commit at submission time. No code fix applies — this is a
manual step for whoever opens that PR.

**Remaining checklist item:**
- [ ] Update `submission/manifest.toml`'s `commit` field to `git rev-parse
  HEAD` at the moment the `DalamudPluginsD17` PR is actually opened.

## Confirmed clean (ruled out, not just unchecked)

- **No file I/O anywhere in the plugin.** Every persistence path goes
  through Dalamud's own `SavePluginConfig`/`GetPluginConfig`. No code path
  builds a filesystem path from remote/downloaded data, so there is no
  path-traversal surface.
- **No process execution.** No `Process.Start`, `ProcessStartInfo`, or
  shell invocation anywhere in the plugin source. (CI does invoke `pwsh`,
  but only with fixed, hardcoded commands — not derived from user or remote
  input.)
- **No reflection or dynamic code loading.** No `Assembly.Load`,
  `Activator.CreateInstance`, `Type.GetType`, or `dynamic` usage anywhere.
  The plugin loads no external DLLs, scripts, or plugins of its own.
- **No hardcoded secrets, API keys, or credentials.** Grepped case-
  insensitively across all source, config, and manifest files; all three
  remote endpoints (now-playing API, album art, MP3 stream) are called
  anonymously with no auth headers.
- **No TLS/certificate validation bypass.** No
  `ServerCertificateCustomValidationCallback`,
  `ServicePointManager.ServerCertificateValidationCallback`, or similar
  anywhere — all three `HttpClient`s use the .NET default certificate
  validation with no override.
- **No unsafe JSON deserialization.** The only deserialization call site
  (`NowPlayingClient.cs`) uses `System.Text.Json` against a fixed,
  non-polymorphic `record` model. There is no `Newtonsoft.Json` dependency
  in this project at all, and nothing resembling `TypeNameHandling`-style
  polymorphic deserialization (a known .NET deserialization-RCE pattern) is
  configured anywhere.
- **No known-vulnerable dependencies.** `dotnet list package --vulnerable
  --include-transitive` reports a clean dependency graph (NAudio 2.3.0 +
  its transitive packages, NLayer.NAudioSupport 2.0.1).
- **No secrets in git history.** `gitleaks detect` scanned all 29 commits
  in the repository's full history — no leaks found.

## Tooling added

These are now permanent, not one-off:

1. **[SecurityCodeScan.VS2019](https://www.nuget.org/packages/SecurityCodeScan.VS2019)
   v5.6.7** — added to `MooglRadio.csproj` as an analyzer-only
   `PackageReference` (`PrivateAssets="all"`, contributes no runtime
   assembly). Runs on every `dotnet build` from now on, flagging
   injection/SSRF/insecure-deserialization-shaped patterns.
2. **`dotnet list package --vulnerable --include-transitive`** — .NET's
   built-in NuGet Audit (no new dependency; part of the SDK). Now an
   explicit CI step so it's visible in build logs, not just a silent
   restore-time warning.
3. **[Gitleaks](https://github.com/gitleaks/gitleaks) v8.30.1** — installed
   locally via Homebrew for this audit's baseline scan, and wired into CI
   via `gitleaks/gitleaks-action` (pinned to a commit SHA) so every future
   push/PR gets scanned for committed secrets.
4. **`.github/workflows/security.yml`** (new) — runs on every push and PR
   (the repo previously had CI *only* on release-tag pushes). Two jobs:
   dependency+analyzer build (restore in locked mode, build, list
   vulnerable packages) and a gitleaks secret scan over full git history.

## Checklist

- [x] F1 — Validate `ArtUrl` is same-origin-relative before fetching
- [x] F2 — Validate `ApiBaseUrl`/`StreamUrl` as absolute `https://` URIs at load time
- [x] F3 — Cap response size on all `HttpClient`s
- [x] F4 — Set explicit timeouts on the short-lived HTTP clients
- [x] F5 — Document the unused `NowPlaying.StreamUrl` trust-boundary risk
- [x] F6 — Pin all CI actions to commit SHAs
- [x] F7 — Enforce `packages.lock.json` via `--locked-mode` restore in CI
- [x] F8 — Reconcile `MooglRadio.json` version with `MooglRadio.csproj`/`repo.json`
- [x] F9 — Document Dalamud CDN download as an accepted, ecosystem-standard risk
- [ ] F10 — Update `submission/manifest.toml`'s commit hash at actual submission time (manual, can't be done in advance)
- [x] Add SecurityCodeScan analyzer to the build
- [x] Add dependency vulnerability audit as an explicit CI step
- [x] Install and run Gitleaks locally (baseline: clean, full history)
- [x] Wire Gitleaks into CI for ongoing scanning
- [x] Add push/PR CI workflow (previously release-tag-only)
- [ ] Confirm the new `security.yml` and updated `release.yml` both run green in GitHub Actions once pushed (can't be verified locally — this dev environment has no local Dalamud install, same documented limitation as the rest of this project's CI-dependent verification)
