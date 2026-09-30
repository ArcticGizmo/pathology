# PATHology — Implementation plan

A Windows desktop app that diagnoses the machine and user `PATH` for security and correctness problems,
scores its health, and explains every finding. Avalonia 12 on .NET 10, in the same shape as `../emuwren`,
themed Nord (Dark), released with Velopack through a tag-triggered GitHub Actions pipeline hardened the way
`../perch`'s is.

See **[capabilities.md](capabilities.md)** for the full capability set. This plan delivers it in milestones.

---

## Decisions

| Topic | Decision |
|---|---|
| **v1.0 scope** | **Read-only diagnosis only.** Discovery, every symptom in capabilities §2, the health score, and what/why/fix explanations. Nothing in v1.0 writes to the registry, the file system or ACLs. |
| **Later milestones** | M6: remediation and safe apply. M7: CLI and reporting. M8: fleet (baseline, drift, Intune). |
| **Health score** | Start at 100 and subtract weighted points per finding. Findings that share a root cause are deduplicated and cost once. **Any Critical caps the overall score at 49%.** Security, Correctness and Hygiene sub-scores sit under the ring. |
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
  Pathology.Core/                 net10.0, NO Avalonia, NO P/Invoke — pure model + detectors + scoring
    Model/                        PathSnapshot, PathEntry, DirectoryFacts, Perspective, AccessResult
    Normalisation/                expansion, case, trailing slash, 8.3 → long, hygiene tokeniser
    Detection/                    one IDetector per symptom row, Finding, Severity, RootCause
    Shadowing/                    PATHEXT-aware command resolution + shadow report
    Scoring/                      HealthScore, weights table, bands
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
2. **Evaluate** (`Pathology.Core`): the detectors, shadowing and scoring are pure functions of the snapshot.

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
      `LowBrush` (Info), `SeverityInfoBrush` (TextMuted), `HealthyBrush` (Success)
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

### Model (`Pathology.Core/Model`)
- [ ] `PathScope` (Machine, User), `RegistryValueKind` (`REG_SZ`, `REG_EXPAND_SZ`)
- [ ] `RawPathValue`: scope, unexpanded string, value kind, length
- [ ] `PathEntry`: scope, index, raw text, expanded text, normalised key, hygiene defects, `DirectoryFacts` ref
- [ ] `DirectoryFacts`: exists, is-directory, attributes, reparse point + final target, drive type
      (fixed/removable/network/UNC/mapped), owner SID, DACL as SDDL, nearest existing ancestor
      (for missing dirs), `AccessResult` per perspective
- [ ] `Perspective`: CurrentUserUnelevated, CurrentUserElevated, System, StandardUser
- [ ] `AccessResult`: can add files, can add subdirectories, WRITE_DAC, WRITE_OWNER, and the ACE/owner that grants it
- [ ] `PathSnapshot`: timestamp, OS build, both raw values, env vars by scope, the effective PATH, the current
      process PATH, `PATHEXT`, entries, and a directory-facts map. It serialises to JSON.
- [ ] **Snapshot redaction** (`SnapshotRedactor`): replace the username, user SID, machine name and profile
      path with placeholders before any snapshot is written as a test fixture or exported. The org policy
      forbids PII in fixtures and bug reports.

### Readers (`Pathology.Windows`, behind Core interfaces)
- [ ] `IRegistryPathReader`: `RegistryKey.GetValue(..., DoNotExpandEnvironmentNames)` plus `GetValueKind`
      for HKLM `...\Session Manager\Environment` and `HKCU\Environment`. Also reads every other variable
      at both scopes, for the expansion checks.
- [ ] `IEffectiveEnvironmentReader`: `CreateEnvironmentBlock(currentToken, bInherit: false)` gives the PATH
      a **new** process would get. Keep the current process's PATH too, so a stale-Explorer divergence can be reported.
- [ ] `IDirectoryProbe`:
  - [ ] `SetThreadErrorMode(SEM_FAILCRITICALERRORS)` around probes, so a removable drive never pops "insert disk"
  - [ ] Attributes via `GetFileAttributesEx`. Reparse targets via `CreateFile(FILE_READ_ATTRIBUTES,
        FILE_FLAG_BACKUP_SEMANTICS)` then `GetFinalPathNameByHandle`, which opens a handle but writes nothing.
  - [ ] Owner and DACL via `GetNamedSecurityInfo` (OWNER | DACL | LABEL) and SDDL
  - [ ] Nearest existing ancestor for missing entries (walk up without creating anything)
  - [ ] Drive type via `GetDriveType`. Mapped letters via `QueryDosDevice` / `WNetGetConnection`.
  - [ ] **Never touch UNC or network paths by default.** Opening `\\attacker\share` sends SMB auth (an NTLM
        hash leak), so they are classified from the string alone. Probing them is an opt-in Setting.
  - [ ] Short-name expansion via `GetLongPathName`, for normalisation
- [ ] `ITokenPerspectives`: builds the SID set for each perspective
  - [ ] **CurrentUserUnelevated**: groups from the current (filtered) token, with Administrators deny-only honoured
  - [ ] **CurrentUserElevated**: groups from `TokenLinkedToken`. Readable unelevated at identification level.
        Equal to unelevated when UAC is off or the user isn't an admin.
  - [ ] **System**: `S-1-5-18`, Administrators, Everyone, Authenticated Users
  - [ ] **StandardUser**: a synthetic unknown user SID + Everyone, Users, Authenticated Users, INTERACTIVE, LOCAL
- [ ] `IAccessEvaluator`: `AuthzInitializeContextFromSid` + `AuthzAddSidsToContext` +
      `AuthzAccessCheck` against the captured security descriptor. This is authoritative (deny ordering,
      inheritance, OWNER RIGHTS, CREATOR OWNER, integrity label) without ever writing. Owner-implied
      `WRITE_DAC` is recorded separately so the "owned by non-admin" detector can explain it.
- [ ] `SnapshotCapturer`: orchestrates the readers and reports progress into the operation checklist

### Tests
- [ ] Windows integration tests: create temp dirs, **set crafted ACLs on those temp dirs only**, then assert the
      `AccessEvaluator` results for each perspective (Users-writable, owner-only, deny-overrides, inherited from parent)
- [ ] Junction and symlink test in the temp dir (a junction needs no admin)
- [ ] A snapshot round-trips through JSON; the redactor removes every PII field

---

## Milestone 2 — Symptom detectors

Each detector is a pure `IDetector.Detect(PathSnapshot) → IEnumerable<Finding>` in its own type, with
fixture-backed tests (a positive case, a negative case and an edge case at minimum).

`Finding`: stable ID, category (Security / Correctness / Hygiene), severity (Critical / High / Medium /
Low / Info), scope, affected entries, the perspectives it applies to, a **root-cause key** (for
deduplication), and `What` / `Why` / `Fix` text. In v1.0 the fix is advisory only.

### Security
- [ ] `SEC-01` Machine PATH dir writable by non-admin principals → **Critical** (System perspective victim,
      StandardUser attacker)
- [ ] `SEC-02` Dir owned by a non-admin (implicit `WRITE_DAC`) → **High**; the fix text includes an ownership reset
- [ ] `SEC-03` Missing machine dir whose nearest existing ancestor is creatable by non-admins (phantom dir) → **Critical**
- [ ] `SEC-04` Writable entry ordered before `%SystemRoot%\System32` / `%SystemRoot%` → separate **shadowing
      risk** rating. Uses PATHEXT precedence (`.COM`/`.BAT` beat `.EXE`) to list which built-ins could be shadowed.
- [ ] `SEC-05` Permissive ACL inherited from a drive root → one root-cause finding covering every affected
      dir. Its children are grouped under it, not reported one by one.
- [ ] `SEC-06` User PATH dir writable by *other* users → **Medium**, suggest relocating under the profile
- [ ] `SEC-07` User-writable dirs reachable from elevated sessions → **UAC exposure summary**
      (a Medium finding plus a Health panel)
- [ ] `SEC-08` Junction or symlink whose **target** is writable → assessed against the target and reported
      at the target's severity, with a "looks safe, isn't" note
- [ ] `SEC-09` UNC path, mapped drive letter or removable drive → **Medium**, listing the contexts where
      it breaks or is hijackable (SYSTEM doesn't see mapped drives; elevated sessions don't either unless
      `EnableLinkedConnections` is set)

### Correctness
- [ ] `COR-01` Machine PATH references a variable defined only at user scope → **High** (error)
- [ ] `COR-02` `%VAR%` inside a `REG_SZ` value → **High**; fix: convert to `REG_EXPAND_SZ`
- [ ] `COR-03` Relative, `.`, or empty entries → **High** (CWD-dependent resolution)
- [ ] `COR-04` Per-user profile paths in machine PATH → **Medium**; recommend moving to user PATH
- [ ] `COR-05` Dead / non-existent entries → **Low** (upgraded by `SEC-03` when the phantom condition holds;
      the two are deduplicated by root-cause key)
- [ ] `COR-06` Duplicates after normalisation (expansion, case, trailing slash, 8.3) → **Low**; says which copy wins
- [ ] `COR-07` Competing executables across dirs → **Info** per command, feeding the Shadowing page
- [ ] `COR-08` Length headroom → **Medium** at ≥ 80% of the limit, **High** at ≥ 95%. Checks the 2047-char
      effective-PATH threshold that older tools choke on, the 32,767 env-block limit, and flags the `setx`
      1024 truncation signature (a value exactly 1024 chars long).

### Hygiene
- [ ] `HYG-01` Stray quotes → **Low**
- [ ] `HYG-02` Leading or trailing whitespace → **Low**
- [ ] `HYG-03` Doubled separators (`;;`) or doubled backslashes → **Low**
- [ ] `HYG-04` Trailing backslash inconsistency (only as input to duplicate detection) → **Info**

### Configured vs effective
- [ ] `CFG-01` Effective PATH (new process) ≠ expansion of the registry values → **Medium**, with a diff
- [ ] `CFG-02` Current process / Explorer PATH is stale compared with the registry → **Info** ("sign out or
      restart Explorer"; M6's `WM_SETTINGCHANGE` broadcast fixes this)

### Shadowing engine (`Pathology.Core/Shadowing`)
- [ ] Enumerate each existing dir's files matching `PATHEXT` (captured in the snapshot as file names only)
- [ ] Resolve each command name the way **cmd** does: walk PATH in order, and within each dir try the
      extensions in PATHEXT order. Note System32 first.
- [ ] Note where **PowerShell** differs (aliases, functions and cmdlets come before PATH; the `.ps1` handling)
      as annotations. Full PowerShell semantics are out of scope.
- [ ] `ShadowReport`: command → winner + hidden copies + whether the winner's dir is writable

---

## Milestone 3 — Health scoring

- [ ] `SeverityWeights`: a single, table-driven, unit-tested source of truth. Starting values:

  | Severity | Points per finding (after root-cause dedup) |
  |---|---|
  | Critical | 30 |
  | High | 15 |
  | Medium | 6 |
  | Low | 2 |
  | Info | 0 |

- [ ] Sub-score per category = `max(0, 100 − Σ points in category)`
- [ ] Overall = Security 50% + Correctness 35% + Hygiene 15%, then:
  - [ ] **any Critical → cap at 49**
  - [ ] any High → cap at 79
- [ ] Bands: **90–100 Healthy** (Nord14 green) · **70–89 Fair** (Nord13) · **50–69 Needs attention**
      (Nord12) · **0–49 At risk** (Nord11)
- [ ] `HealthScore` also carries the counts by severity, the top N findings, and an "if you fixed X, you'd reach Y%" hint
      (the score recomputed without the highest-weight root cause). That hint is the hook that draws people in.
- [ ] Tests: the caps, the dedup (one drive-root cause over five dirs costs once), clamping, and band edges

---

## Milestone 4 — UI

Layout follows emuwren: a 200px nav on the left, and pages that are a `ScrollViewer` holding a
`StackPanel MaxWidth≈860` with a title, help text and a Re-scan button at the top right.

### Nav
- [ ] Brand `pathology` + tagline "windows PATH health"
- [ ] Section **Diagnose**: Health, Findings (count badge = Critical + High), Entries (entry count), Shadowing
- [ ] Section **Understand**: Learn
- [ ] Bottom: update button (when available) → Settings → About (version)
- [ ] The scan runs on launch and on Re-scan, sharing one snapshot across every page; progress goes into the
      `OperationProgressViewModel` checklist (ported)

### Health (landing page)
- [ ] **Score ring**: a custom `HealthRing` control (arc drawn in `Render`), coloured by band, with an animated
      count-up from 0 on the first scan and a tween on re-scan
- [ ] Band label + severity summary: "AT RISK · 2 critical · 5 warnings"
- [ ] Security / Correctness / Hygiene sub-score bars
- [ ] "Fix the top issue to reach **N%**" hint
- [ ] Top 3–5 findings as cards (severity glyph, one-line what, scope pill). Clicking one opens Findings
      filtered to it.
- [ ] Panels: **UAC exposure** (`SEC-07`), **Length headroom** (a bar for each limit), **Scan info** (time,
      perspectives evaluated, entry counts, whether UNC probing was skipped)
- [ ] Empty/healthy state: a celebratory 100% with "Nothing to fix"

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
- [ ] `pathology scan`: prints findings and the score as plain text; exit code = count of Critical + High.
      This is a dev and verification aid, **not** the M7 reporting contract.
- [ ] `pathology render <dir>`: every page (plus posed states such as healthy, at risk and empty) to PNG via
      Avalonia.Headless, into `./captures/render`
- [ ] `pathology check-update`

### Quality gates
- [ ] Every detector has fixture tests. Fixtures are **redacted** snapshots captured from real machines,
      re-captured rather than hand-edited.
- [ ] Score-model tests
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
2. **Score weights**: 30/15/6/2/0, category split 50/35/15, Critical cap 49, High cap 79. **Tune them
   later** against real `scan` and `render` output; they live in one tested table (`SeverityWeights`).
3. **v1.0 fix text is advisory prose only.** There is no copy-command button; runnable commands arrive with M6.
4. **Headless `scan` verb ships in v1.0** as a dev and verification aid (emuwren's `doctor` style), ahead of M7.
5. **No Dependabot.** SHA-pinned actions and NuGet versions are bumped by hand.
