# PATHology — working notes

A Windows desktop app that diagnoses the machine and user `PATH` for security and correctness problems,
scores its health, and explains every finding. Avalonia 12 on .NET 10, in the same shape as `../emuwren`,
themed Nord (Dark) via `ArcticGizmo.Avalonia.Palette`, released with Velopack from a `v*` tag.

- **[docs/capabilities.md](docs/capabilities.md)** — what the tool does.
- **[docs/implementation-plan.md](docs/implementation-plan.md)** — the milestone checklist (M0 → M8) and
  every decision behind it. Tick items off there as they land.
- **[docs/packaging.md](docs/packaging.md)** — release, install and update mechanics.

---

## 🛑 A scan is side-effect free — keep it that way

PATHology's credibility rests on being safe to run on any machine, including one with EDR watching. So:

- **Writability comes from ACL evaluation, never from probing.** No test files, no "can I create this
  folder?", no creating a phantom directory to see if it works. `AuthzAccessCheck` over the captured
  security descriptor is the only way (M1).
- **No `icacls`, `reg`, `setx`, `whoami` shell-outs to read state.** Read through the Windows APIs.
- **Never touch UNC or network paths unless the user opted in.** Opening `\\host\share` authenticates to
  that host over SMB and hands it an NTLM hash. Classify them from the string alone by default.
- **Removable drives must not prompt.** Wrap probes in `SEM_FAILCRITICALERRORS` so an empty card reader
  never pops "insert a disk".

## 🛑 Nothing writes the PATH, the registry or an ACL from automation (M6 onward)

From M6 the app can apply fixes. Those writers — the registry PATH writer, the ACL lock-down, the elevated
helper — run **only from production code driven by a real user click**:

- Never from a test, the headless renderer, a CLI verb, or a `Bash`/`PowerShell` call. Not "with a safe
  subset", not "just to check it works".
- Never invoke the elevated helper speculatively (it raises a UAC prompt) or to "check elevation works".
- Tests cover writers against fakes and temp directories only. A test that touches the real registry, the
  real `PATH`, or a real `Program Files` / drive-root ACL is a bug in the test, however green it goes.
- Tests **may** set ACLs on temp directories they created, to exercise the access evaluator.
- Real targets are resolved in exactly one place — the composition root, `AppServices` — and injected
  everywhere else, so a test physically can't be pointed at the real machine.

## 🛑 No PII in fixtures, captures or exports

Snapshot fixtures (`tests/Pathology.Tests/Fixtures/`) and exported snapshots carry real PATHs, which carry
usernames, SIDs, machine names and profile paths. **Redact before writing** (`SnapshotRedactor`, M1) —
replace them with placeholders like `C:\Users\<user>`. Never commit an unredacted capture. Posed renderer
states use made-up paths (`C:\Users\you\...`), never this machine's.

---

## Verifying changes safely

These are the read-only paths. Prefer them.

```sh
dotnet build pathology.slnx
dotnet test pathology.slnx

# Every page, the changelog and the update button, to PNG. Read-only.
dotnet run --project src/Pathology.App -- render ./captures/render

# The installer's parsing + ASCII-purity checks (no network, installs nothing).
powershell -NoProfile -File tools/test-install.ps1
```

The renderer builds real view-models over a temp store and performs **no** mutating action. If a page ever
needs a mutation to render interestingly, pose the view-model's state directly rather than executing the
operation.

## Conventions

- `Pathology.Core` is OS-agnostic, has no Avalonia reference and no P/Invoke, and treats warnings as errors.
  Windows reads live in `Pathology.Windows` (M1) behind Core interfaces so they can be faked.
- **Capture once, evaluate purely.** A scan captures an immutable, serialisable `PathSnapshot`; detectors,
  shadowing and scoring are pure functions of it, each in its own type with fixture-backed tests.
- Blocking calls run off the UI thread via `AppServices.RunAsync`.
- Brushes: paint with `{DynamicResource …}` using the palette's keys (`FormBgBrush`, `PanelBgBrush`, …) or
  PATHology's severity tokens (`Theming/NordTheme.cs`). Never declare a brush with a palette key in
  `App.axaml` or replace one in `Application.Resources` — the engine recolours them in place.
- `install.ps1` stays **pure ASCII with no BOM**; run `tools/test-install.ps1` after editing it. It must
  never use `Invoke-Expression`.
- The dev instance (Debug build) uses `%LOCALAPPDATA%\PATHology Data (Dev)` and wears a pink `- DEV` badge.
- Screenshots and captures go to `./captures/` — never `%TEMP%` or a scratchpad.
