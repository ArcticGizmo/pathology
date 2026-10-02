# Changelog

All notable changes to PATHology are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

### Added

- **Fixing, on the entry it's about** — PATHology now fixes what it finds, not just tuts at it. Pick an entry
  on the System or User page and the panel beside it says what's wrong, why it matters, and the fix to stage.
  Right-click for the same. Move it, reorder it, edit it or delete it while you're there. Nothing is staged until
  you ask (or press *Stage the recommended fixes*), and removed lines stay put, struck through, so you can change
  your mind where you made it.
- **Review** — everything staged, in one place, before anything happens: the ratings before and after, every
  entry added, removed or moved, every folder's new permissions, and every command that would start running
  something else. Then one Apply.
- **Folder lock-downs** — writable PATH folders get their permissions trimmed to read & execute for everyone
  but administrators, applied by the app itself, with the equivalent icacls commands on offer to copy.
  A folder inside your own profile gets moved to your user PATH instead, because locking you out of your own
  folder is technically secure.
- **One UAC prompt per apply, and only when needed** — changes to your user PATH never ask. The elevated helper
  checks the change it's handed against a hash, writes nothing else, and is believed only after its work has
  been read back.
- **Move a system entry to your user PATH** — the copy goes into your user PATH before the system PATH lets
  it go, so an apply that stops half-way leaves the entry in both rather than in neither. Decline the UAC prompt
  and the copy is taken back out.
- **History and Undo** — every apply backs up what it replaces first, records each write, and can be undone in
  one click. The undo is undoable too, for the indecisive.
- **Nothing changed since the scan, or nothing changes** — an apply that finds your PATH or a folder different
  from what the scan saw refuses the lot rather than guessing.
- **The PATHology app** — a Dashboard, the System and User PATHs, Shadowing and History, with Learn for the
  curious, in Nord (Dark), all drawn from one read-only scan that runs when the app opens (switchable in
  Settings) and again on Re-scan.
- **Dashboard** — the three ratings side by side and what's holding each one up, then every problem worst
  first: the ones PATHology can fix (each opens its entry), and the ones it can only explain (each unfolds to
  say what to do about it).
- **System and User** — each PATH in search order, exactly as stored with what each `%VARIABLE%` expands to
  beneath, stray quotes and spaces picked out where they hide, and, folded away until you want it, who can
  write each folder from five points of view.
- **Shadowing** — type a command, see which file runs and which ones it hides. Windows commands that
  something else is already answering for are listed, which is usually news.
- **Learn** — seven short articles on how Windows really finds commands and DLLs, linked from the problems
  that need them. Entirely optional reading, which is why it's at the bottom.
- **Export a redacted snapshot** from Settings, for bug reports. Script and tool names outside Windows, and
  variable names PATH doesn't use, are swapped for placeholders too, so a bug report doesn't double as a list
  of the projects you work on.
- **Folders you can write in your own PATH stop counting against you** — malware running as you can already
  edit your user PATH, or your PowerShell profile, so the folders it could plant in open no new door. The UAC
  exposure summary is a note now, never a problem, and goes away under Administrator Protection, where
  elevation has its own PATH anyway. A folder of yours ahead of System32 is Low: an accident waiting to happen,
  not an attack.
- **Sandboxed programs** — a fifth point of view: your account at Low integrity, the way a browser or reader
  sandbox runs. A PATH folder it can write is a way out of the sandbox, and that is a real boundary, so it's
  reported as one. It's also gratifyingly rare.
- **A check for System32 missing from PATH**, after it turned out a PC with no PATH at all was rated
  spotless. Technically there was nothing wrong with it. There was nothing there.
- **An app icon**, crisp at every size, which is more than most PATHs can say.
- **In-app updates** — a nav button when a release lands; install and restart from About.
- **"What's new" after an update**, with an off switch in Settings.
- **Network paths are left alone** — a Settings switch, off by default, decides whether PATHology may open
  UNC shares and mapped drives. Off means no stranger's server gets a look at your password hash.
- **`pathology snapshot`** — writes a redacted snapshot of this machine's PATH for bug reports, and refuses
  to write at all if anything identifying survives the redaction.
- **`pathology scan`** — 24 checks for what's wrong with your PATH, worst first, each with what, why and how
  to fix it. `--redact` makes the output fit to paste somewhere.
- **Health ratings** — security, correctness and hygiene, each rated by its worst problem: clean, low, medium
  or high. No percentages, so ten tidy-ups can't hide one open door.

---
