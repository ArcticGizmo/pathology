<h1 align="center">PATHology</h1>

<p align="center">
 <img src="./landing-icon.png" width="150" />
</p>

<p align="center">
<strong>Windows PATH health</strong>
</p>

<br>

PATHology diagnoses security and correctness problems in the machine and user
`PATH`, explains why each one matters, and (from a later release) produces safe, reversible fixes.

> **Status: pre-1.0.** Diagnosis is complete: scanning, the health ratings, findings, entries, shadowing and the
> Learn articles. It's read-only; safe fixes arrive later. See [docs/implementation-plan.md](docs/implementation-plan.md).

What it will look for — writable machine-PATH folders that SYSTEM searches, phantom directories, entries
that shadow built-in commands, `%VAR%`s that never expand, duplicates, dead entries, length limits and more
— is in [docs/capabilities.md](docs/capabilities.md).

## Install

```powershell
irm https://raw.githubusercontent.com/ArcticGizmo/pathology/main/install.ps1 | iex
```

The script fetches the latest release's installer, verifies it against the published `SHA256SUMS.txt`, and
runs it. PATHology installs per-user to `%LocalAppData%\Pathology` with no admin rights, and updates itself
from the About page. It always runs unelevated — a scan never needs admin.

## Build from source

Needs the .NET 10 SDK (pinned in `global.json`).

```sh
dotnet build pathology.slnx
dotnet test pathology.slnx
dotnet run --project src/Pathology.App
```

Release and packaging details are in [docs/packaging.md](docs/packaging.md).

## Icons

The logo's single source of truth is [`pathology.svg`](pathology.svg). The raster assets (the window, `.exe`
and installer icon, and the header image above) are generated from it, so nothing that ships depends on an
SVG renderer:

```powershell
tools/gen-icons.ps1     # or: dotnet run --project tools/IconGen -c Release
```

Run that after editing `pathology.svg`, then commit the regenerated `src/Pathology.App/Assets/pathology.ico`
and `landing-icon.png`. It renders with Svg.Skia, which stays crisp at 16–48px where GDI+ goes muddy. Add
`-- --preview captures/icon-frames.png` to the `dotnet run` form for an enlarged sheet of the small frames.
`tools/IconGen` is deliberately kept out of `pathology.slnx`.
