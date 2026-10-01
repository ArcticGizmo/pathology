If you're an administrator with UAC on, you have two tokens. Everyday programs run with the **filtered** one, which has the Administrators group switched off. "Run as administrator" starts a program with the **elevated** one.

Both run as *you*. So an elevated program's environment is built from the same machine PATH **and the same user PATH** as everything else you run, and it searches the same folders.

## Why that matters

Anything under your profile is writable from your ordinary, unelevated session: `AppData`, `scoop\shims`, `.dotnet\tools`, a `bin` folder you made. That's normal. But it means malware running as you, *without* admin rights, can drop a `git.exe` or a DLL into one of those folders and wait. The next time you run something elevated that searches PATH, it runs the planted file as administrator, and no UAC prompt asks about it.

Microsoft doesn't treat UAC as a security boundary, and says so. It's still the one most PCs rely on, which is why PATHology shows this as the **UAC exposure** panel, separately from the folders other users can write.

## The baseline

Windows puts `%LOCALAPPDATA%\Microsoft\WindowsApps` (where app execution aliases such as `winget` and `python` live) in every user's PATH. On its own that's how Windows ships, so PATHology notes it rather than marking you down for it.

## Mapped drives and elevation

Drive letters you map are per logon session. Your elevated session is a different one, so it doesn't see your mapped drives unless the `EnableLinkedConnections` policy is set, and SYSTEM never does. A PATH entry on a mapped drive simply vanishes for those programs, or worse, points at whatever that letter means to them.

## What helps

- Install tools for all users under `C:\Program Files`, which only administrators can write.
- Keep writable folders *after* the Windows ones on PATH.
- For regular admin work, use a separate administrator account, so your everyday PATH never reaches elevated sessions at all.
