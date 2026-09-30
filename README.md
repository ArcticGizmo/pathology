# PATHology

**Windows PATH health.** PATHology diagnoses security and correctness problems in the machine and user
`PATH`, explains why each one matters, and (from a later release) produces safe, reversible fixes.

> **Status: early.** v0.1 is the app shell and release pipeline. Scanning, findings and the health score
> land over the next milestones — see [docs/implementation-plan.md](docs/implementation-plan.md).

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
