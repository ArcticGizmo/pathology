# Changelog

All notable changes to PATHology are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

### Added

- **The PATHology app** — Health, Findings, Entries, Shadowing and Learn, in Nord (Dark), all drawn from one
  read-only scan that runs when the app opens (switchable in Settings) and again on Re-scan.
- **Health** — the three ratings side by side, what's holding each one up, and the worst few problems, plus
  UAC exposure, how close PATH is to its length limits, and what the scan looked at.
- **Findings** — every problem worst first, one row per cause, filterable, each with what, why, how to fix
  it and the permission that makes it possible. Copy details pastes it into a ticket.
- **Entries** — both PATHs in search order, with who can write each folder from four points of view, and
  stray quotes and spaces picked out where they hide.
- **Shadowing** — type a command, see which file runs and which ones it hides. Windows commands that
  something else is already answering for are listed, which is usually news.
- **Learn** — seven short articles on how Windows really finds commands and DLLs, linked from the findings
  that need them.
- **Export a redacted snapshot** from Settings, for bug reports.
- **An app icon**, crisp at every size, which is more than most PATHs can say.
- **In-app updates** — a nav button when a release lands; install and restart from About.
- **"What's new" after an update**, with an off switch in Settings.
- **Network paths are left alone** — a Settings switch, off by default, decides whether PATHology may open
  UNC shares and mapped drives. Off means no stranger's server gets a look at your password hash.
- **`pathology snapshot`** — writes a redacted snapshot of this machine's PATH for bug reports, and refuses
  to write at all if anything identifying survives the redaction.
- **`pathology scan`** — 23 checks for what's wrong with your PATH, worst first, each with what, why and how
  to fix it. `--redact` makes the output fit to paste somewhere.
- **Health ratings** — security, correctness and hygiene, each rated by its worst problem: clean, low, medium
  or high. No percentages, so ten tidy-ups can't hide one open door.

---
