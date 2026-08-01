# Instructions for Claude

## Before tagging a release

This repo has been bitten by version-mismatch install failures before.
Before creating/pushing any `vX.Y.Z` tag, verify **all three** of these
are bumped to the same `X.Y.Z.0` value in the same commit:

1. `MooglRadio/MooglRadio.csproj` — `<Version>`
2. `MooglRadio/MooglRadio.json` — `AssemblyVersion`
3. `repo.json` — `AssemblyVersion` (repo root, not the one under `MooglRadio/`)

Run this before tagging to confirm they agree:

```bash
grep -H "<Version>" MooglRadio/MooglRadio.csproj
grep -H "AssemblyVersion" MooglRadio/MooglRadio.json repo.json
```

All three lines must show the identical version. If they don't, fix the
stragglers before committing — don't tag with a mismatch. See the
"Releasing a new version" section in README.md for the full flow.
