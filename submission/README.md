# Dalamud repo submission staging

This folder isn't consumed by anything in this repo (not the build, not
`repo.json`, not CI) — it's just where the files for a future PR against
[DalamudPluginsD17](https://github.com/goatcorp/DalamudPluginsD17) live
until that PR is actually opened, so they don't get lost or drift from
the plugin's real state.

## To submit

1. ~~Add `images/icon.png`~~ — done, hand-made, 512x512.
2. Update `manifest.toml`'s `commit` field to the actual commit hash
   being submitted (whatever `git rev-parse HEAD` is at submission
   time — the one in there now is just a snapshot from when this draft
   was written).
3. Fork (or use GitHub's web editor on) `goatcorp/DalamudPluginsD17`,
   create `testing/live/MooglRadio/manifest.toml` and
   `testing/live/MooglRadio/images/icon.png` from this folder, and open
   a PR.
4. In the PR description, disclose AI tool usage per the
   [AI usage policy](https://dalamud.dev/plugin-publishing/ai-policy/) —
   this plugin was built with significant AI assistance under human
   direction, with real in-game testing driving each fix (see the
   confirmation log in the main [README](../README.md)). That's
   disclosable, not by itself disqualifying — "entirely AI-generated
   with no meaningful human involvement" is what gets auto-rejected.
5. Worth a quick ask in the Dalamud Discord first: `NowPlayingClient`
   polls `/api/now-playing` on a 15s timer the whole time the plugin is
   loaded, not per user action. The restrictions doc bans "automatic
   polling or requests without direct user action," which reads like
   it's aimed at automating game actions/servers rather than a plugin
   refreshing its own companion service's display — and
   [CrystalRadio](https://github.com/Saevath/CrystalRadio) (a real
   published radio plugin) suggests this pattern is fine — but better
   to confirm than find out via rejection.
