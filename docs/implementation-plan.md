# PATHology — Implementation plan

A Windows desktop app that diagnoses the machine and user `PATH` for security and correctness problems,
rates its health, and explains every finding. Avalonia 12 on .NET 10, in the same shape as `../emuwren`,
themed Nord (Dark), released with Velopack through a tag-triggered GitHub Actions pipeline hardened the way
`../perch`'s is.

See **[capabilities.md](capabilities.md)** for the full capability set. This plan delivers it in milestones.

---

## Decisions

| Topic | Decision |
|---|---|
| **v1.0 scope** | **Read-only diagnosis only.** Discovery, every symptom in capabilities §2, the category ratings, and what/why/fix explanations. Nothing in v1.0 writes to the registry, the file system or ACLs. |
| **Later milestones** | M6: remediation and safe apply. M7: CLI and reporting. M8: fleet (baseline, drift, Intune). |
| **Health** | *Changed in M3.* Security, Correctness and Hygiene are each rated by their **worst problem**: Clean, Low, Medium or High. No number and no overall verdict. Findings that share a root cause are one problem. Info findings are notes and never rate a category. |
| **Editing (M6)** | Apply generated fixes, plus light editing: reorder, remove, add, and move an entry between user and machine scope. Every change goes through the same dry-run → diff → backup → apply pipeline. |
| **Elevation** | The app **always runs unelevated** (`asInvoker`). The SYSTEM and elevated perspectives come from ACL evaluation against synthetic SID sets, so a scan never needs admin. In M6, machine-scope writes go to an elevated helper (the same exe run with a verb), with one UAC prompt per apply batch. |
| **Pages** | Health (landing), Findings, Entries, Shadowing, Learn. Settings and About are pinned to the bottom of the nav, as in emuwren. |
| **Theme** | `ArcticGizmo.Avalonia.Palette` (the full package with `ThemeManager`), fixed to **Nord (Dark)**. Its tokens map onto emuwren's brush keys, so views are written the same way. No picker in v1.0, but the package leaves room for one. |
| **Release** | Perch's hardened workflow: actions pinned to SHAs, read-only default permissions, separate build and release jobs, and `SHA256SUMS.txt`. Also `global.json`, `.config/dotnet-tools.json` (vpk), and the `install.ps1` one-liner with its ASCII-purity test. **Windows only**, so there is a single build job. |
| **Updates** | Emuwren style: check on launch, a blue "Update available" button above Settings in the nav, and About showing the version, a check button, and download → install → restart. |

---

## Architecture

```
pathology.slnx
global.json                       SDK pin (10.0.4xx, rollForward latestFeature)
.config/dotnet-tools.json         vpk pinned to the Velopack NuGet version
src/
  Pathology.Core/                 net10.0, NO Avalonia, NO P/Invoke — pure model + detectors + ratings
    Model/                        PathSnapshot, PathEntry, DirectoryFacts, Perspective, AccessResult
    Normalisation/                expansion, case, trailing slash, 8.3 → long, hygiene tokeniser
    Detection/                    one IDetector per symptom row, Finding, Severity, RootCause
    Shadowing/                    PATHEXT-aware command resolution + shadow report
    Health/                       HealthReport: each category rated by its worst problem
    Learn/                        explain-mode content (embedded markdown)
    Changelog/                    parser (ported from emuwren)
  Pathology.Windows/              net10.0-windows — every OS read sits here, behind Core interfaces
    RegistryPathReader            raw, unexpanded HKLM/HKCU Path + value kind
    EffectiveEnvironmentReader    CreateEnvironmentBlock for a fresh-process PATH
    DirectoryProbe                exists, attributes, reparse target, drive type, owner, DACL (SDDL)
    AccessEvaluator               AuthzAccessCheck per perspective (no write probing)
    TokenPerspectives             current user filtered / linked-elevated / SYSTEM / generic standard user
  Pathology.App/                  Avalonia 12, CommunityToolkit.Mvvm, Velopack, Palette
tests/
  Pathology.Tests/                xunit; detectors run against redacted JSON snapshot fixtures
  Pathology.Windows.Tests/        read-only integration tests against temp dirs with crafted ACLs
tools/
  test-install.ps1  gen-icons.ps1
```

**The key idea is to capture once and evaluate as a pure function.** A scan has two phases:

1. **Capture** (`Pathology.Windows`): read everything into an immutable, serialisable `PathSnapshot`. That
   covers raw registry values, env vars at each scope, the effective PATH, and a `DirectoryFacts` for
   every entry and its nearest existing ancestor (including per-perspective `AccessResult`s).
2. **Evaluate** (`Pathology.Core`): the detectors, shadowing and ratings are pure functions of the snapshot.

The benefit is that every detector is unit-tested from JSON fixtures, `render` can pose any machine state,
and M8's baseline/drift is a diff of two snapshots.

---

## Milestone 0 — Scaffold and release pipeline

Get a signed-off, installable empty shell released first, so every later milestone ships through a
working pipeline.

### Repo and solution
- [x] `pathology.slnx` with Core, App and Tests. *`Pathology.Windows` and `Pathology.Windows.Tests` are added
      in M1, when they get their first code. The App and Tests already target `net10.0-windows`, so adding them changes nothing else.*
- [x] `global.json` (copied from perch) and `.config/dotnet-tools.json` with `vpk` pinned to the `Velopack`
      package version (1.2.0)
- [x] `Directory.Build.props`: `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors` on Core
- [x] `.gitignore` (bin/obj/publish/releases/captures) and `.gitattributes`
- [x] `CLAUDE.md` with the safety rules (see [Safety rules](#safety-rules-for-claudemd))
- [x] `README.md`, `CHANGELOG.md` (Keep a Changelog)
- [x] `LICENSE`: MIT, © ArcticGizmo (matching the palette package)
- [x] Port emuwren's `.claude/skills/bump-version` skill. *It now treats "no tags yet" as the first release,
      so `0.1.0` ships as `v0.1.0` rather than being bumped to `0.1.1`.*

### App shell (ported from emuwren)
- [x] `Pathology.App.csproj`: `WinExe`, `AssemblyName=pathology`, `Product=PATHology`,
      `AvaloniaUseCompiledBindingsByDefault`, app manifest, embedded `CHANGELOG.md` *(no icon yet; see below)*
- [x] `app.manifest`: `asInvoker`, PerMonitorV2 DPI, `longPathAware`, Windows 10+ supportedOS
- [x] `Program.Main`: `VelopackApp.Build().Run()` **first**, then the headless verbs, then Avalonia
- [x] `ViewLocator`, `ViewModelBase`, `PageViewModel`, `NavHeaderViewModel`, `MainWindowViewModel`.
      *`Navigator` is deferred until a page needs to jump to another (M4: Health → Findings).*
- [x] `MainWindow.axaml`: 200px left nav (brand + tagline, section headers, nav count badges, bottom-pinned
      update button / Settings / About), `ContentControl` for the current page
- [x] `AppServices` composition root, plus `RunAsync` to take blocking work off the UI thread
- [x] `AppProfile` dev/installed split: Debug writes to `%LOCALAPPDATA%\PATHology Data (Dev)` and shows the
      pink `- DEV` badge. *The store is **`PATHology Data`**, not `PATHology`: paths are case-insensitive, so
      that would be Velopack's install folder (`%LOCALAPPDATA%\Pathology`), which an uninstall deletes
      wholesale, taking settings and M6's PATH backups with it. A test guards this.*
- [x] `SettingsStore` / `JsonFile` (ported). *Settings hold only what works today (the changelog toggle);
      scan-on-launch and UNC probing arrive with the scanner in M1.*
- [x] Placeholder pages: Health, Findings, Entries, Shadowing, Learn (one shared `PlaceholderPageViewModel`
      that says what each page will show and which milestone brings it), plus the real Settings and About

### Nord (Dark) theme
- [x] Reference `ArcticGizmo.Avalonia.Palette` **0.3.0** (the latest; namespace `ArcticGizmo.Avalonia.Palette`)
- [x] Initialise `ThemeManager` with `nord-dark` (`Theming/NordTheme.cs`), called outside the desktop-lifetime
      check so the headless renderer is themed too. `RequestedThemeVariant="Dark"`.
- [x] Brush keys: **no mapping needed.** The package publishes emuwren's house keys (`FormBgBrush`,
      `PanelBgBrush`, `FgBrush`, … `DevBrush`) as built-in tokens, plus nav tokens (`NavBgBrush`,
      `NavItemActiveBgBrush`, …) and `OnAccentBrush`. A test asserts every key the views use is a built-in.
- [x] Severity brushes registered as palette-*derived* tokens, so they stay inside the package's WCAG-AA gate:
      `CriticalBrush` (Danger), `HighBrush` (Danger⊕Warning, Nord's aurora orange), `MediumBrush` (Warning),
      `LowBrush` (Info), `SeverityInfoBrush` (TextMuted), `HealthyBrush` (Success). *Re-mapped in M3 for three
      levels: see there.*
- [x] Port `Styles/Common.axaml`. Hard-coded colours are re-pointed at tokens: the active nav, the primary
      button's text (`OnAccentBrush`), and hover states.

### Updates (emuwren style)
- [x] Port `Updates/UpdateChecker` (GitHub Releases source, with a `PATHOLOGY_UPDATE_FEED` override)
- [x] Launch check → `HasUpdate` → accented nav button → About
- [x] About page: version, "Check for updates", "Download & install" (apply + restart), "View changelog"
- [x] "What's new" window on first launch after an update (`LastSeenVersion`). *The renderer now joins
      a wrapped bullet's continuation lines. emuwren's rendered them as a stray paragraph.*
- [x] `pathology check-update` headless verb, plus `pathology render <dir>`

### Release pipeline (perch hardening, Windows only)
- [x] `.github/workflows/release.yml` on a `v*` tag:
  - [x] `permissions: contents: read` at the top level, and every action pinned to a commit SHA (perch's SHAs)
  - [x] `build` job (windows-latest): checkout (`persist-credentials: false`) → setup-dotnet from
        `global.json` → `dotnet tool restore` → **`dotnet test`** → version from tag → **CHANGELOG has a
        section for the tag** → `dotnet publish` win-x64 self-contained single-file → `dotnet vpk pack` →
        upload `releases/`. *No `--icon` yet.*
  - [x] `release` job (`needs: build`, `contents: write`): download → refuse duplicate names → fail if
        `Pathology-win-Setup.exe` is missing → generate `SHA256SUMS.txt` outside `dist/` and move it in →
        `sha256sum -c` → step summary → `softprops/action-gh-release` with `generate_release_notes`
  - [x] Verified locally: the same publish + `vpk pack` produces `Pathology-win-Setup.exe`, the nupkg,
        portable zip and feed files, and the published single-file exe renders every page
- [x] `install.ps1` ported (`PATHOLOGY_VERSION` / `PATHOLOGY_REPO`, **pure ASCII, no BOM**). *Its entry point
      is skipped when `PATHOLOGY_INSTALL_NO_RUN` is set, so the test dot-sources it instead of using
      emuwren's `Invoke-Expression` loader.*
- [x] `tools/test-install.ps1`: ASCII purity, BOM, the entry-point guard, and manifest parsing (8 checks, passing on 5.1)
- [x] `docs/packaging.md`
- [ ] App icon: generate a prompt with the `icon-prompt` skill → `tools/gen-icons.ps1` → `Assets/pathology.ico`,
      then add `ApplicationIcon`, the window `Icon` and `vpk pack --icon`
- [ ] **Exit criterion:** create the GitHub repo and push, run `/bump-version`, tag `v0.1.0`, confirm the
      one-liner installs it, then tag `v0.1.1` and confirm the in-app update applies

---

## Milestone 1 — Discovery (capture a `PathSnapshot`)

Everything here is **read-only and side-effect free**: no test files, no directory creation, no
`icacls`/`reg` shell-outs.

> **Closed 2026-09-30.** 157 Core + 31 Windows tests green. On this machine `pathology snapshot` captured 49
> entries → 55 directories (44 exist) with 176/176 access checks and no leaks. Developer Mode is off here, so
> the three symlink tests return early (logged "skipped:"); run them on a Developer Mode machine sometime.

### Model (`Pathology.Core/Model`)
- [x] `PathScope` (Machine, User), `PathValueKind` (`REG_SZ`, `REG_EXPAND_SZ`). *Named `PathValueKind`, not
      `RegistryValueKind`, to avoid clashing with `Microsoft.Win32.RegistryValueKind` in `Pathology.Windows`.*
- [x] `RawPathValue`: scope, unexpanded string, value kind, length
- [x] `PathEntry`: scope, index, raw text, expanded text, form (absolute/relative/UNC/…), normalised key,
      hygiene defects, referenced + unresolved variables, and `ProbePath` (the `DirectoryFacts` key)
- [x] `DirectoryFacts`: exists, is-directory, attributes, reparse tag + target (+ whether it leads to the
      network), drive type (fixed/removable/mapped network/UNC/not mounted) + mapped/subst target, owner SID,
      SDDL (owner, group, DACL, label), long name, nearest existing ancestor, `AccessResult` per perspective
- [x] `Perspective`: CurrentUserUnelevated, CurrentUserElevated, System, StandardUser, plus
      `PerspectiveIdentity` (the SID set with attributes) kept in the snapshot
- [x] `AccessResult`: the granted mask (can add files / subdirectories, WRITE_DAC, WRITE_OWNER), owner-implied
      `WRITE_DAC`, and the granting ACEs (inherited or not)
- [x] `PathSnapshot`: timestamp, host (OS build, UAC state, `EnableLinkedConnections`), both raw values, env
      vars by source (machine, user, volatile, new process, current process), the effective PATH, the current
      process PATH, `PATHEXT`, perspectives, entries, directories. Serialises to JSON (`PathSnapshotJson`,
      camelCase, enums as names), and `Write` refuses an unredacted snapshot.
      *Env var **names** are all kept; **values** only for what PATH references (transitively) plus a short
      allowlist, because env vars hold tokens.*
- [x] **Snapshot redaction** (`SnapshotRedactor`): rewrites every string. Account SIDs are renumbered
      consistently (`S-1-5-21-0-0-n-RID`, Azure AD `S-1-12-1-0-0-0-n`); profile folders (8.3 forms and other
      users' too), username, machine, domain, UNC hosts, `OneDrive - Org` and emails become placeholders.
      Idempotent. `FindLeaks` reports leftovers by category only.

### Readers (`Pathology.Windows`, behind Core interfaces)
- [x] `IRegistryPathReader`: `DoNotExpandEnvironmentNames` + `GetValueKind`, read-only keys; every variable at
      machine, user and volatile scope
- [x] `IEffectiveEnvironmentReader`: `CreateEnvironmentBlock(token, bInherit: false)`, plus the current process's
- [x] `IDirectoryProbe`:
  - [x] `SetThreadErrorMode(SEM_FAILCRITICALERRORS)` around every probe
  - [x] Attributes via `GetFileAttributesEx`. Reparse targets are read from the link's own reparse data
        (`FSCTL_GET_REPARSE_POINT`), **not** `GetFinalPathNameByHandle`, which would follow the link and could
        reach a UNC target. Relative symlinks resolve against their folder.
  - [x] Owner, DACL and label via `GetSecurityInfo` on an `NtOpenFile(READ_CONTROL)` handle. *`CreateFileW`
        adds `SYNCHRONIZE | FILE_READ_ATTRIBUTES` to every request, which an owner-only folder refuses even
        though the owner implicitly holds `READ_CONTROL`. That's exactly the SEC-02 case.*
  - [x] Nearest existing ancestor for missing entries (walked by `SnapshotCapturer` in Core, from strings)
  - [x] Drive type via `GetDriveType`; mapped letters via `WNetGetConnection`; subst via `QueryDosDevice`
  - [x] **Never touches UNC or network paths by default**: UNC, mapped drives, a subst onto a share, and any
        path whose route passes through a link to one (each component is checked before anything is opened).
        Opt-in via the new Settings toggle (`ProbeNetworkPaths`, off). *The route check fails closed: a
        component it can't read (access denied, unreadable reparse tag) stops the probe, and a local subst
        drive is walked as the folder it stands for.*
  - [x] Short-name expansion via `GetLongPathName`, only for paths containing `~`
- [x] `ITokenPerspectives`: current token + `TokenLinkedToken` (either direction, so running elevated works
      too); SYSTEM and a synthetic standard user from fixed SID lists, each with an integrity label SID
- [x] `IAccessEvaluator`: `AuthzInitializeContextFromSid(AUTHZ_SKIP_TOKEN_GROUPS)` + `AuthzAddSidsToContext` +
      `AuthzAccessCheck(MAXIMUM_ALLOWED)`. *AuthZ does **not** apply the mandatory label to a SID-built
      context (a test proved it), so no-write-up is applied on top from the SDDL's `ML` ACE.*
- [x] `SnapshotCapturer` (in Core, over the interfaces): reports `CaptureProgress` per step for M4's checklist.
      `WindowsCapture.Create` wires the real readers, and `AppServices.Capture` is the one caller.
- [x] *Added:* `pathology snapshot [file]` writes a redacted snapshot (fixture material for M2, and the M4
      bug-report export). It refuses to write if `FindLeaks` finds anything, and prints counts only.

### Tests
- [x] Windows integration tests on temp dirs with crafted ACLs: Users-writable, owner-only, deny-overrides,
      inherited from parent. SDDL-level evaluator tests add OWNER RIGHTS, inherit-only, deny-only
      Administrators and the integrity label.
- [x] Junction test (always runs); symlink-to-UNC, relative and root-relative symlink tests (need Developer
      Mode, otherwise they return early and log "skipped"); an uninspectable folder on the route stops the probe
- [x] A snapshot round-trips through JSON; the redactor removes every PII field; a read-only capture of the real
      machine redacts with no leaks. Windows test assertions use `Quiet.Same`, so a failure never prints
      a real path or SID.

---

## Milestone 2 — Symptom detectors

Each detector is a pure `IDetector.Detect(DetectionContext) → IEnumerable<Finding>` in its own type, with
fixture-backed tests (a positive case, a negative case and an edge case at minimum). *`DetectionContext`
wraps the snapshot with what every rule needs worked out once (entries resolved through links, the System32
position, whether you're an admin, the shadow report), so it's still a pure function of the snapshot.*
`Diagnoser.Diagnose` runs them all, ranks the findings and groups them by root cause (`FindingGroup`, led by
its worst member).

`Finding`: stable ID, category (Security / Correctness / Hygiene), severity (High / Medium / Low, plus
Info for notes; *Critical was folded into High in M3*), scope, affected entries, the perspectives it applies to, a **root-cause key** (for
deduplication), and `What` / `Why` / `Fix` text. In v1.0 the fix is advisory only. *Also `Evidence` (the
granting ACE, the contexts that break, a diff) and a `Learn` topic for M4's deep links. `Key` = rule + subject.*

> **Done 2026-09-30.** 241 Core + 33 Windows tests. Tests use `TestMachine`, a DSL that describes a made-up
> machine and runs it through the real capturer, rather than committed JSON fixtures. Checked against this
> machine through `pathology scan --redact`: every finding above Info traced to a real cause; the false
> positives that turned up (CFG-01/02 on repeats Windows drops, COR-03 on Windows' own trailing `;`, a
> redaction bug that re-cased entry keys) are fixed and tested.

### Security
- [x] `SEC-01` Machine PATH dir writable by non-admin principals → **High** (System perspective victim,
      StandardUser attacker). *Also when only you can write it; the text says whether that's an escalation (you're
      a standard user) or a UAC bypass (you're an admin). Write access that comes only from ownership is left to SEC-02.*
- [x] `SEC-02` Dir owned by a non-admin (implicit `WRITE_DAC`) → **High**; the fix text includes an ownership reset.
      *Skipped when an `OWNER RIGHTS` ACE caps the owner.*
- [x] `SEC-03` Missing machine dir whose nearest existing ancestor is creatable by non-admins (phantom dir) → **High**
- [x] `SEC-04` Writable entry ordered before `%SystemRoot%\System32` / `%SystemRoot%` → separate **shadowing
      risk** rating. Uses PATHEXT precedence (`.COM`/`.BAT` beat `.EXE`) to list which built-ins could be shadowed.
      *High (Medium when only an admin's own session can write it). Shares SEC-01's root cause: one lock-down fixes both.*
- [x] `SEC-05` Permissive ACL inherited from a drive root → one root-cause finding covering every affected
      dir. Its children are grouped under it, not reported one by one. *`ProgramData` is a second source (it
      hands Users write access to its subfolders). Windows, Program Files and the profiles are excluded.*
- [x] `SEC-06` User PATH dir writable by *other* users → **Medium**, suggest relocating under the profile.
      *Also a missing user dir that other users could create.*
- [x] `SEC-07` User-writable dirs reachable from elevated sessions → **UAC exposure summary**
      (a Medium finding plus a Health panel). *Only for an admin with a split token. **Info** when the only
      exposed folder is the stock `WindowsApps`, so a fresh install isn't marked down.*
- [x] `SEC-08` Junction or symlink whose **target** is writable → assessed against the target and reported
      at the target's severity, with a "looks safe, isn't" note. *Link entries are judged only here, not by SEC-01/06.*
- [x] `SEC-09` UNC path, mapped drive letter or removable drive → **Medium**, listing the contexts where
      it breaks or is hijackable (SYSTEM doesn't see mapped drives; elevated sessions don't either unless
      `EnableLinkedConnections` is set). *Also a link to the network and an unmounted drive letter (grouped
      with its COR-05).*

### Correctness
- [x] `COR-01` Machine PATH references a variable defined only at user scope → **High** (error). *Also a
      variable nothing defines, in either scope.*
- [x] `COR-02` `%VAR%` inside a `REG_SZ` value → **High**; fix: convert to `REG_EXPAND_SZ`
- [x] `COR-03` Relative, `.`, or empty entries → **High** (CWD-dependent resolution). *Empty entries are one
      **Medium** finding per value: Windows skips them; it's Unix-style shells (Git Bash, MSYS2, Cygwin) that
      can read them as the current directory. The single trailing `;` is ignored, since Windows writes one itself.*
- [x] `COR-04` Per-user profile paths in machine PATH → **Medium**; recommend moving to user PATH
- [x] `COR-05` Dead / non-existent entries → **Low** (upgraded by `SEC-03` when the phantom condition holds;
      the two are deduplicated by root-cause key). *Also an entry that names a file.*
- [x] `COR-06` Duplicates after normalisation (expansion, case, trailing slash, 8.3) → **Low**; says which copy wins
- [x] `COR-07` Competing executables across dirs → **Info**, feeding the Shadowing page. *One finding per
      (winning folder, hidden folder) pair rather than per command: two JDKs side by side would otherwise
      be 36 findings.*
- [x] `COR-08` Length headroom → **Medium** at ≥ 80% of the limit, **High** at ≥ 95%. Checks the 2047-char
      effective-PATH threshold that older tools choke on, the 32,767 env-block limit, and flags the `setx`
      1024 truncation signature (a value exactly 1024 chars long).

### Hygiene
- [x] `HYG-01` Stray quotes → **Low**
- [x] `HYG-02` Leading or trailing whitespace → **Low**
- [x] `HYG-03` Doubled separators (`;;`) or doubled backslashes → **Low**. *Doubled backslashes and forward
      slashes; `;;` is an empty entry, which COR-03 reports.*
- [x] `HYG-04` Trailing backslash inconsistency (only as input to duplicate detection) → **Info**

*Each hygiene rule is one finding per value, listing every affected entry: the fix is one edit.*

### Configured vs effective
- [x] `CFG-01` Effective PATH (new process) ≠ expansion of the registry values → **Medium**, with a diff.
      *Compared with repeats removed: Windows drops a user entry that repeats a machine one when it builds
      the new-process PATH, which this machine showed.*
- [x] `CFG-02` Current process / Explorer PATH is stale compared with the registry → **Info** ("sign out or
      restart Explorer"; M6's `WM_SETTINGCHANGE` broadcast fixes this)

### Shadowing engine (`Pathology.Core/Shadowing`)
- [x] Enumerate each existing dir's files matching `PATHEXT` (captured in the snapshot as file names only).
      *`IDirectoryProbe.ListFiles` → `DirectoryFacts.CommandFiles`, `PATHEXT` plus `.ps1`, entry folders only,
      never a network folder or one reached through a link to the network.*
- [x] Resolve each command name the way **cmd** does: walk PATH in order, and within each dir try the
      extensions in PATHEXT order. Note System32 first.
- [x] Note where **PowerShell** differs (aliases, functions and cmdlets come before PATH; the `.ps1` handling)
      as annotations. Full PowerShell semantics are out of scope.
- [x] `ShadowReport`: command → winner + hidden copies + whether the winner's dir is writable. *Plus
      `Resolve(name)` for "which python?", `Competing`, `Builtins` and the folders that couldn't be listed.*

### Brought forward from M5
- [x] `pathology scan [--redact] [--details] [--all] [--from snapshot.json]`: findings grouped by root cause,
      exit code = High problems. `--redact` diagnoses the redacted snapshot, so output can be shared.
      *The category ratings arrived with M3.*

---

## Milestone 3 — Health ratings

> **Changed 2026-09-30: ratings, not a score.** The first cut was the planned 0–100 score (points per problem,
> a 50/35/15 split, caps at 49 and 79). On this machine it came out at 35% with security pinned at 0: the
> security deductions far exceeded 100, so fixing any one security problem didn't move the number, and the
> "fix X to reach Y%" hint pointed at a correctness fix while four Criticals stood. The number was replaced with
> per-category ratings, and the Critical level was folded into High.

- [x] Severities are **Low / Medium / High**. `Info` stays as a *note* (competing tools, a stale Explorer
      PATH, the stock WindowsApps exposure) that never rates a category. What was Critical (any user → SYSTEM)
      is High; the finding's text still says whether it's an escalation or a UAC bypass.
- [x] Each category (Security, Correctness, Hygiene) is rated by its **worst problem**: Clean, Low, Medium or
      High. Nothing is summed, so a pile of small problems never outweighs one serious one, and fixing the
      worst always shows. **No overall verdict**: the three ratings stand side by side.
- [x] A problem is a root-cause group, counted once, in its worst finding's category at that finding's severity
      (a phantom directory that's also a dead entry is one Security problem).
- [x] `CategoryHealth.Holding`: the problems at the category's rating, i.e. what to fix to lower it.
      `AfterHolding`: what it drops to once they're fixed. These replace the percentage hint.
- [x] `HealthReport` / `HealthRater` in `Pathology.Core/Health`, pure over the diagnosis's groups.
- [x] Tests: worst-not-sum, notes never rate, counts per problem, one drive-root cause over five dirs is one
      problem, what holds a rating and what it drops to, and a stock Windows PATH is clean.
- [x] `pathology scan` prints each category's rating, its counts and "fix N to reach …"; the exit code is
      the number of High problems. On this machine: Security High (9 high · 2 medium), Correctness High
      (2 high · 1 medium · 3 low), Hygiene Low, 12 notes.
- [x] Theme: `CriticalBrush` is gone. High → red (Danger), Medium → aurora orange, Low → yellow (Warning),
      notes → muted, clean → green.

---

## Milestone 4 — UI

Layout follows emuwren: a 200px nav on the left, and pages that are a `ScrollViewer` holding a
`StackPanel MaxWidth≈860` with a title, help text and a Re-scan button at the top right.

### Nav
- [ ] Brand `pathology` + tagline "windows PATH health"
- [ ] Section **Diagnose**: Health, Findings (count badge = High problems), Entries (entry count), Shadowing
- [ ] Section **Understand**: Learn
- [ ] Bottom: update button (when available) → Settings → About (version)
- [ ] The scan runs on launch and on Re-scan, sharing one snapshot across every page; progress goes into the
      `OperationProgressViewModel` checklist (ported)

### Health (landing page)
- [ ] **Three category cards** (Security, Correctness, Hygiene), side by side: the rating as a coloured word
      (CLEAN / LOW / MEDIUM / HIGH), the counts beneath ("9 high · 2 medium"), and "Fix 9 to bring it to Medium"
- [ ] Each card's worst problems (up to 3) as rows (severity glyph, one-line title, scope pill). Clicking one
      opens Findings filtered to it; clicking the card opens Findings filtered to the category.
- [ ] A notes line under the cards ("12 notes: competing tools and the like"), linking to Findings' Info filter
- [ ] Panels: **UAC exposure** (`SEC-07`), **Length headroom** (a bar for each limit), **Scan info** (time,
      perspectives evaluated, entry counts, whether UNC probing was skipped)
- [ ] Clean state: all three cards green with "Nothing to fix"

### Findings
- [ ] Severity-ranked list with filters: severity, category, scope, perspective
- [ ] Master/detail (emuwren's list pane capped at `ListPaneMaxWidth`): the detail shows **What / Why / Fix**,
      affected entries, the perspectives it applies to, the granting ACE or owner, and a "Learn more" link into Learn
- [ ] Root-cause groups expand to show their child entries
- [ ] "Copy details" (plain text, for a ticket)

### Entries
- [ ] Machine and user PATH, each in resolved order (index, raw → expanded, value kind shown once per scope)
- [ ] Per entry: status glyphs, a **perspective matrix** (4 columns: writable / not / n/a), owner, reparse
      target, drive type, and the findings attached to it
- [ ] Toggle between raw and expanded, and to show hygiene defects inline (highlighted characters)
- [ ] Uses `CharWrapTextBlock` (ported) for long paths

### Shadowing
- [ ] Search box ("which python?"): resolution chain, winner, hidden copies
- [ ] List of commands with more than one provider, sortable by "winner is writable" first
- [ ] Highlight built-ins (`where`, `cmd`, `powershell`, `net`, …) that are shadowable by a writable earlier entry

### Learn
- [ ] Embedded markdown articles: DLL search order, PATHEXT precedence, UAC and PATH inheritance, how
      Windows builds a new process's PATH, REG_SZ vs REG_EXPAND_SZ, phantom directories, why not `setx`
- [ ] Rendered with Markdig (as perch does), deep-linkable from findings

### Settings
- [ ] Scan on launch (on by default)
- [ ] Probe UNC/network paths (**off** by default, with a warning explaining the auth leak)
- [ ] Show the What's-new window after updates
- [ ] Export a redacted snapshot (for bug reports)

### About
- [ ] Version, update check / download & install, changelog viewer, repo link

---

## Milestone 5 — Verification and v1.0 release

### Headless verbs (read-only, as in emuwren)
- [x] `pathology scan`: prints findings and the category ratings as plain text; exit code = count of High
      problems. This is a dev and verification aid, **not** the M7 reporting contract. *Landed in M2 and M3.*
- [ ] `pathology render <dir>`: every page (plus posed states such as clean, all-High and empty) to PNG via
      Avalonia.Headless, into `./captures/render`
- [ ] `pathology check-update`

### Quality gates
- [ ] Every detector has fixture tests. Fixtures are **redacted** snapshots captured from real machines,
      re-captured rather than hand-edited.
- [x] Rating-model tests (M3)
- [ ] Render the golden states and review them by eye
- [ ] Manual test matrix: admin user with UAC on, standard user, and a machine with a deliberately
      writable `C:\Tools` in machine PATH (set up by hand in a VM)
- [ ] `CHANGELOG.md` 1.0.0 → tag `v1.0.0`

---

## Later milestones (outline only)

### M6 — Remediation and safe apply
- [ ] Remediation planner: a cleaned PATH value per scope, a recommended ordering (Windows dirs → locked-down
      → writable last), scope moves, and `icacls` lock-down commands split into *as you* and *needs admin*
- [ ] **Resolution diff**: re-run the shadowing engine on the proposed snapshot and show which commands change
- [ ] Light editor on Entries: reorder, remove, add, move between scopes, all feeding the same plan
- [ ] Dry run by default; explicit confirmation to apply
- [ ] Automatic backup of both values (raw + value kind) before any write, and one-step rollback (a History page)
- [ ] Writer preserves `REG_EXPAND_SZ`, never truncates, never uses `setx`, and re-reads to verify
- [ ] Broadcast `WM_SETTINGCHANGE` (`SendMessageTimeout`, "Environment")
- [ ] Elevated helper: `pathology apply-elevated <plan.json>` launched via `runas` with one UAC prompt per
      batch; it applies **only** the machine-scope part of a signed/hashed plan file
- [ ] `CLAUDE.md` rules extended: writers are never invoked from tests or probes (the emuwren rule)

### M7 — CLI and reporting
- [ ] Console-subsystem companion or `AttachConsole` strategy (a `WinExe` can't write to the console cleanly)
- [ ] Output formats: terminal table, JSON (a versioned schema), HTML report
- [ ] A stable **exit-code contract** by highest severity

### M8 — Fleet
- [ ] Baseline snapshot + drift detection (diff of two snapshots), alerting when an installer adds a bad entry
- [ ] A paired Intune/RMM detection + remediation script generated from the same detectors

---

## Safety rules for CLAUDE.md

- A scan is **side-effect free**: no test files, no directory creation, no registry writes, no `icacls`/`reg`
  shell-outs. Writability comes from DACL evaluation, never from probing.
- **Never touch UNC or network paths** unless the user opted in (SMB auth leak).
- Tests may set ACLs **only on temp directories they created**. They never touch the real registry, the real
  PATH, or real `Program Files` / drive-root ACLs.
- Fixtures and exported snapshots are **redacted**: no usernames, SIDs, machine names or profile paths.
- (M6 onward) Registry and ACL writers run only from production code driven by a real user click. They are
  never called from a test, a probe, `render`, or a Bash/PowerShell call, and never used speculatively to
  "check elevation works".
- Screenshots and captures go to `./captures/`.

---

## Resolved

1. **Identity**: repo `ArcticGizmo/pathology`, exe `pathology.exe`, packId `Pathology`, product name
   "PATHology", installed to `%LocalAppData%\Pathology`.
2. **No numeric score** (changed in M3): each category is rated by its worst problem, Low / Medium / High,
   with no overall verdict. The planned 30/15/6/2/0 points saturated on a real machine (see M3).
3. **v1.0 fix text is advisory prose only.** There is no copy-command button; runnable commands arrive with M6.
4. **Headless `scan` verb ships in v1.0** as a dev and verification aid (emuwren's `doctor` style), ahead of M7.
5. **No Dependabot.** SHA-pinned actions and NuGet versions are bumped by hand.
