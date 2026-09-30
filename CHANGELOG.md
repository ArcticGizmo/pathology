# Changelog

All notable changes to PATHology are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

### Added

- **The PATHology shell** — Health, Findings, Entries, Shadowing and Learn, in Nord (Dark). All empty,
  all honest about it.
- **In-app updates** — a nav button when a release lands; install and restart from About.
- **"What's new" after an update**, with an off switch in Settings.
- **Network paths are left alone** — a Settings switch, off by default, decides whether PATHology may open
  UNC shares and mapped drives. Off means no stranger's server gets a look at your password hash.
- **`pathology snapshot`** — writes a redacted snapshot of this machine's PATH for bug reports, and refuses
  to write at all if anything identifying survives the redaction.
- **`pathology scan`** — 23 checks for what's wrong with your PATH, worst first, each with what, why and how
  to fix it. `--redact` makes the output fit to paste somewhere. The app's pages catch up in a later release.
- **Health ratings** — security, correctness and hygiene, each rated by its worst problem: clean, low, medium
  or high. No percentages, so ten tidy-ups can't hide one open door.

---
