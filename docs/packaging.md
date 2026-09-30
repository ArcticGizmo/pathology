# Packaging & updates

PATHology is packaged for Windows with [Velopack](https://velopack.io). A release produces an installer
(`Pathology-win-Setup.exe`), a portable zip, and the update feed the app reads. Updates are
**notify-then-opt-in**: on launch the app only *tells* you a newer version exists; the About page downloads
and installs it. Builds are **not code-signed** yet.

## Prerequisites

- The .NET SDK pinned in [`global.json`](../global.json).
- The `vpk` CLI, pinned as a local tool in [`.config/dotnet-tools.json`](../.config/dotnet-tools.json) to the
  same version as the `Velopack` NuGet package the app references (currently 1.2.0). Restore it with:
  ```sh
  dotnet tool restore
  ```
  `vpk` warns that a newer version exists. Ignore it unless you bump the `Velopack` package too — the CLI and
  the library should move together.

## Build a release locally

From the repo root, publish exactly what CI publishes, then pack it:

```sh
dotnet publish src/Pathology.App/Pathology.App.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded \
  -p:Version=0.1.0 -o publish/

dotnet vpk pack --packId Pathology --packTitle "PATHology" --packVersion 0.1.0 \
  --packDir publish/ --mainExe pathology.exe --outputDir releases/
```

`releases/` then holds `Pathology-win-Setup.exe`, `Pathology-<version>-full.nupkg`,
`Pathology-win-Portable.zip`, `RELEASES`, `releases.win.json` and `assets.win.json`. Packing a later
`--packVersion` into the same directory appends to the feed and generates a delta. (`publish/` and
`releases/` are git-ignored.)

> `vpk` verifies that `VelopackApp.Build().Run()` is the first call in `Program.Main` — that hook handles
> the install/update lifecycle and must stay first.

## Cutting a release (CI)

Releases are automated by [`.github/workflows/release.yml`](../.github/workflows/release.yml), triggered by
**pushing a `v*` tag**. It is hardened the way perch's is:

- **Read-only by default.** The workflow's `permissions` are `contents: read`; only the final `release` job
  gets `contents: write`, and it checks out and builds nothing.
- **Every action is pinned to a commit SHA**, with the version in a trailing comment. There's no
  Dependabot, so bump them by hand — resolve the new tag's commit, update the SHA and the comment together.
- **`build`** (windows-latest): SDK from `global.json`, `dotnet tool restore`, **tests**, a check that
  `CHANGELOG.md` has a section for the tag, the single-file publish, `vpk pack`, and an artifact upload.
- **`release`** (ubuntu, `needs: build`): flattens the artifacts, refuses duplicate names, fails if
  `Pathology-win-Setup.exe` is missing, generates `SHA256SUMS.txt` from the exact bytes being uploaded,
  verifies it with `sha256sum -c`, writes it to the step summary, and creates the GitHub Release.

The flow for a release:

1. Run the **`/bump-version`** skill — it sets `<Version>` in `src/Pathology.App/Pathology.App.csproj` and
   writes the new `CHANGELOG.md` section from the commits since the last tag. It does not commit or tag.
2. Commit those two files.
3. Tag and push: `git tag vX.Y.Z && git push origin vX.Y.Z`. The tag is the source of truth for the
   version; CI stamps it into the build with `-p:Version`.

## Changelog

`CHANGELOG.md` (repo root, [Keep a Changelog](https://keepachangelog.com/) format) is **embedded** into the
app (`Pathology.CHANGELOG.md`). On the first launch after an update, the app shows a "What's new" window with
the entries newer than the version that last ran (driven by `LastSeenVersion`, and switchable off in
**Settings**). It's viewable any time from **About → View changelog**. Parsing lives in
`Pathology.Core/Changelog/ChangelogParser.cs`.

## Install

The primary install path is the PowerShell one-liner ([`install.ps1`](../install.ps1)):

```powershell
irm https://raw.githubusercontent.com/ArcticGizmo/pathology/main/install.ps1 | iex
```

It resolves the GitHub release, fetches `SHA256SUMS.txt` and `Pathology-win-Setup.exe`, verifies the
installer against the manifest (deleting it rather than running it on any mismatch), then hands off to
Velopack's setup. Downloading via PowerShell rather than a browser skips the mark-of-the-web, so it avoids the
SmartScreen "Windows protected your PC" dialog. Pin a version with `$env:PATHOLOGY_VERSION = '0.1.0'` before
the pipe, or a fork with `$env:PATHOLOGY_REPO`.

> **`install.ps1` must stay pure ASCII** (no BOM) — Windows PowerShell 5.1 decodes it as the system codepage,
> and a stray em dash becomes a curly quote that silently terminates a string. Run `tools/test-install.ps1`
> after editing it; it asserts ASCII purity and exercises the manifest parsing. The test dot-sources the
> script with `PATHOLOGY_INSTALL_NO_RUN=1`, which skips its entry point — no `Invoke-Expression` involved.

`Setup.exe` installs per-user to `%LocalAppData%\Pathology` (no admin) with Start Menu and Desktop shortcuts.
Settings live separately in `%LocalAppData%\PATHology Data`, so an uninstall — which removes the install
folder wholesale — doesn't take them (or, from M6, your PATH backups) with it.

## Update notifications

On launch the app checks the release feed; if a newer version exists, an **Update to vX.Y.Z** button appears
in the nav above Settings. It routes to **About**, which shows the installed version, **Check for updates**,
and **Download & install** (applies the update and restarts). Both share `Updates/UpdateChecker.cs`.

- The feed defaults to PATHology's GitHub Releases. `PATHOLOGY_UPDATE_FEED` (a directory path or URL)
  overrides it — point it at a local `releases/` folder to test the flow. A copy not installed by Velopack
  (e.g. `dotnet run`) reports "not applicable" and never offers an update.
- Probe it headlessly: `"%LocalAppData%\Pathology\current\pathology.exe" check-update`

## Headless commands

All are **read-only**.

```sh
pathology render <dir>    # every page, the changelog and the update button, to PNG
pathology check-update    # the launch-time update check, printed
pathology snapshot [file] # a redacted snapshot of this machine's PATH (counts only on the console)
pathology scan [--redact] [--details] [--all] [--from file]
                          # the category ratings and findings as text; exit code = High problems
```

## Dev vs installed

A Debug build writes to `%LOCALAPPDATA%\PATHology Data (Dev)` and wears a pink `- DEV` badge, so running from
source can't clobber your installed copy's settings. `PATHOLOGY_DEV=0` forces a Debug build onto the real
store; any other value forces dev. See `Pathology.Core/Store/AppProfile.cs`.

## Not yet done

- **Code signing** — `vpk pack` warns that files are unsigned. Add `--signParams` once a certificate exists.
- **An app icon** — `--icon` (and the window/`ApplicationIcon`) are deliberately absent until one exists.
