<h1 align="center">PATHology</h1>

<p align="center">
 <img src="./landing-icon.png" width="150" />
</p>

<p align="center">
<strong>Windows PATH health</strong>
</p>

<br>

PATHology is a Windows desktop app that checks your machine and user `PATH` for security and correctness
problems, explains each one in plain language, and fixes them safely, with every change backed up and undoable.

It looks for things like system folders that any user can write (a way to get code running as SYSTEM), folders
that don't exist yet but anyone could create, entries that shadow Windows' own commands, `%VAR%`s that never
expand, duplicates, dead entries and values nearing the length limit. The full list, with why each one matters,
is in [docs/capabilities.md](docs/capabilities.md).

## Install

```powershell
irm https://raw.githubusercontent.com/ArcticGizmo/pathology/main/install.ps1 | iex
```

The script downloads the latest release's installer, checks it against the release's `SHA256SUMS.txt`, and
runs it. PATHology installs for your user only, to `%LocalAppData%\Pathology`, with no admin rights. Pin a
version by setting `$env:PATHOLOGY_VERSION = '0.1.0'` first.

When a new version is out, an **Update** button appears in the nav. Updates install from the About page, and a
"What's new" window lists the changes the first time the new version runs.

## What it does

### Diagnoses

- **25 checks** across three categories: **Security**, **Correctness** and **Hygiene**.
- **Each category is rated Clean, Low, Medium or High by its worst problem**, not a score, so ten tidy-ups
  can't hide one open door.
- **Looks from more than one point of view.** A folder can be safe for you and dangerous for SYSTEM. Who can
  write each folder comes from its permissions, owner and integrity label, including through junctions and
  symlinks.
- **Every problem says what's wrong, why it matters and how to fix it**, with a link to a Learn article when
  there's more to it.

### Pages

| Page | What's on it |
|---|---|
| **Dashboard** | The three ratings, *Things to fix* (worst first, each opening its entry), and *Worth knowing* for problems with no automatic fix. |
| **System** / **User** | Each PATH line by line: the text as stored, what it expands to, what's wrong with it, and who can write it. Click a line for its problems, its fixes and the actions on it. |
| **Shadowing** | For each command, which file actually runs and what it hides, plus the commands a writable folder could hijack. |
| **History** | Every change you've applied, how it went, and Undo. |
| **Learn** | Seven short articles: DLL search order, how a command is found, UAC and PATH, how a new process gets its PATH, `REG_SZ` vs `REG_EXPAND_SZ`, phantom directories, and why not `setx`. |

### Fixes

- **Nothing changes until you say.** Fixes are staged on the entry they're about, alongside your own edits
  (move, reorder, edit or delete a line, or move a system entry to your user PATH).
- **Review before applying.** See the ratings before and after, each value's diff, which commands would run a
  different file, and each folder permission change with its `icacls` equivalent to copy.
- **One UAC prompt per apply**, and none at all when only your user PATH changes.
- **Folder lock-downs** remove write access from non-admins on system PATH folders.
- **Writes done properly:** the value kind (`REG_EXPAND_SZ`) is kept, nothing is truncated, `setx` is never
  used, and open programs are told the PATH changed so new shells pick it up without a logoff.
- **Every apply is backed up** to History before the first write, and can be undone (the undo can be undone
  too).
- **Won't apply over changes made since the scan.** If something else edited your PATH in the meantime, it
  asks you to re-scan.

## Safe to run

A scan changes nothing:

- **No test files.** Whether a folder is writable is worked out from its permissions, never by trying to
  write to it.
- **No shell-outs.** Everything is read through the Windows APIs, not `reg`, `icacls` or `whoami`.
- **Network paths are left alone.** A `\\server\share` entry is judged from its text unless you opt in under
  Settings, since opening one hands that server your credentials.
- **No "insert a disk" prompts** from empty card readers.
- **It runs unelevated.** Only applying a system-wide fix asks for admin, through a single UAC prompt.

**Export a redacted snapshot** (Settings) saves the latest scan to attach to an issue. Your username, SIDs,
the PC's name, profile folders, network hosts and project names become placeholders, and nothing is written if
anything identifying is left. Folder names are kept, so give it a read before sharing.

## Where things live

| What | Where |
|---|---|
| The app | `%LocalAppData%\Pathology` |
| Settings and History (your PATH backups) | `%LocalAppData%\PATHology Data` |

Uninstalling removes the app folder only, so your settings and backups survive a reinstall.

## Not yet

- **Code signing.** Releases aren't signed yet, so the UAC prompt shows an unknown publisher. The install
  script avoids the SmartScreen warning a browser download would get.
- **Fleet use.** Baseline and drift detection, and an Intune/RMM detection and remediation script, are planned.

## Build from source

Needs the .NET 10 SDK (pinned in `global.json`).

```sh
dotnet build pathology.slnx
dotnet test pathology.slnx
dotnet run --project src/Pathology.App
```

A Debug build keeps its own settings in `%LocalAppData%\PATHology Data (Dev)` and wears a pink `- DEV` badge, so
it can't disturb an installed copy. `dotnet run --project src/Pathology.App -- render ./captures/render` draws
every page over a made-up PC to PNG without touching this one.

Release, install and update mechanics are in [docs/packaging.md](docs/packaging.md), and the milestone plan is
in [docs/implementation-plan.md](docs/implementation-plan.md). Ground rules for contributors (and for agents)
are in [CLAUDE.md](CLAUDE.md).

### Icons

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
