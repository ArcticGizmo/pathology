# PATHology — Capabilities

PATHology is a Windows PATH health and resolution tool. It diagnoses security and correctness problems in the machine and user `PATH` values, explains why each one matters, and produces safe, reversible fixes.

## 1. Discovery (read-only)

- **Raw registry read** — machine (`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment`) and user (`HKCU\Environment`) `Path`, read *unexpanded*, including the value kind (`REG_SZ` vs `REG_EXPAND_SZ`).
- **Configured vs effective** — compare the stored registry values with the PATH a new process actually receives, and flag any divergence.
- **Token-aware evaluation** — assess each entry from multiple perspectives: current user (unelevated), current user (elevated), SYSTEM, and a generic standard user. The same PATH can be safe from one viewpoint and exploitable from another.
- **ACL analysis, not write-probing** — writability is determined from DACLs, ownership and token groups, never by creating test files. The tool stays side-effect free and EDR-friendly.

## 2. Symptom detection

### Security

| Symptom | Why it matters | Outcome |
|---|---|---|
| Machine PATH directory writable by non-admin principals (Users, Authenticated Users, Everyone, INTERACTIVE) | SYSTEM services searching PATH for a missing DLL will load a planted one — local privilege escalation to SYSTEM | High finding + generated ACL lock-down |
| Directory owned by a non-admin | The owner has implicit `WRITE_DAC`, so it is effectively writable even when the ACL looks clean | Flag, and include an ownership reset in the fix |
| Missing directory in machine PATH whose nearest existing ancestor is creatable | Anyone can create the missing folder and plant binaries (phantom-directory hijack) | High finding; recommend removal |
| Writable entry ordered *before* the Windows system directories | cmd and PowerShell resolve bare commands by walking PATH in order, and `.COM`/`.BAT` beat `.EXE` in PATHEXT — built-in commands can be shadowed | Separate "shadowing risk" rating + reorder suggestion |
| Permissive ACL inherited from the drive root (folders created directly under a drive root) | Identifies the root cause so a single fix covers every affected directory | Report the root cause once rather than per entry; fix at source |
| User PATH directory writable by *other* users | A shared location outside the profile lets other accounts plant binaries into your sessions | Warning; suggest relocating under the profile |
| User-writable directories reachable from elevated sessions | Elevated processes inherit the user PATH, so medium-integrity code can plant binaries that later run elevated (UAC bypass path) | UAC exposure summary |
| Junction / symlink whose target is writable | The entry looks safe, but the real location is not | Resolve reparse points and assess the target |
| UNC path, mapped drive letter or removable drive | May be absent, or attacker-controlled, depending on execution context | Warning with the contexts in which it breaks |

### Correctness

| Symptom | Why it matters | Outcome |
|---|---|---|
| Machine PATH references a variable defined only at user scope | Never expands; a literal, relative entry ends up in the live PATH | Error — move to user scope or inline the value |
| `%VAR%` inside a `REG_SZ` value | Windows will not expand it | Fix — convert the value kind to `REG_EXPAND_SZ` |
| Relative, `.`, or empty entries | Resolve against the current working directory, so behaviour depends on where the process was started | Remove |
| Per-user profile paths (e.g. under `%USERPROFILE%` / `AppData`) in machine PATH | Broken for other users, and SYSTEM ends up searching a user's profile | Recommend moving to user PATH |
| Dead / non-existent entries | Clutter, plus the phantom-directory risk above | Recommend removal |
| Duplicates after normalisation (expansion, case, trailing slash, 8.3 short names) | Bloat, and ambiguity over which copy is authoritative | Merge |
| Multiple directories providing the same executable (competing runtimes / toolchains) | Which version runs depends solely on order | **Shadowing report** — for each command name, which directory wins and which are hidden |
| Value approaching length limits | Truncation corrupts PATH (the classic `setx` 1024-character accident) | Warning with remaining headroom |
| System32 not on PATH (no machine PATH, or its Windows entries never expand) | Windows' own commands stop resolving by name, and scripts and installers that call them fail | Error |
| Hygiene issues — stray quotes, trailing whitespace, doubled separators or backslashes | Causes subtle mismatches and false duplicates | Auto-clean |

## 3. Outcomes

- **Health ratings**: Security, Correctness and Hygiene each rated Clean, Low, Medium or High by their worst problem, plus severity-ranked findings, each with a plain-language *what / why / fix*.
- **Scope recommendations** — which machine entries belong in user scope, which should be removed outright, and which legitimately belong in machine scope but need their ACLs locked down.
- **Recommended ordering** — Windows directories first, then locked-down directories, with writable locations last.
- **Generated remediation** — a cleaned PATH value and `icacls` lock-down commands, split into *can apply as you* and *needs admin*.
- **Resolution diff** — before applying, show which commands would resolve to a different executable afterwards.

## 4. Safe changes

- **Dry-run by default**; applying changes requires explicit confirmation.
- **Automatic backup** of both values before any write, with one-step rollback.
- **Preserve the value kind** (`REG_EXPAND_SZ`), never truncate, and never use `setx`.
- **Broadcast `WM_SETTINGCHANGE`** so new shells pick up changes without a reboot or logoff.
- **Least privilege** — only elevate for machine-scope changes; user-scope fixes never require admin.

## 5. Reporting and fleet use

- ~~Output formats: terminal table, JSON, and HTML report.~~ *Not required: no CLI.*
- ~~**Exit codes by severity** for CI and scripting.~~ *Not required: no CLI.*
- **Baseline and drift detection** — alert when an installer introduces a bad entry.
- **Intune / RMM mode** — a paired detection + remediation script for fleet rollout.
- **Explain mode** — short educational notes on the underlying concepts (DLL search order, PATHEXT precedence, UAC and PATH inheritance) for people learning how this works.
