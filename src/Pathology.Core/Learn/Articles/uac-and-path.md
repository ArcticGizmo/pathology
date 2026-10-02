If you're an administrator with UAC on, you have two tokens. Everyday programs run with the **filtered** one, which has the Administrators group switched off. "Run as administrator" starts a program with the **elevated** one.

Both run as *you*. So an elevated program's environment is built from the same machine PATH **and the same user PATH** as everything else you run, and it searches the same folders.

## Why that matters

Anything under your profile is writable from your ordinary, unelevated session: `AppData`, `scoop\shims`, `.dotnet\tools`, a `bin` folder you made. That's normal. It means malware running as you, *without* admin rights, could drop a `git.exe` or a DLL into one of those folders and wait for you to run something elevated.

## Why it's only a note

Those folders don't give that malware anything it didn't have. Your user PATH is stored in your own part of the registry, which you can write without elevating, so the same malware could simply add a folder of its own to it. Or it could hook your PowerShell profile, which elevated PowerShell loads too, or use one of the many known UAC bypasses. Microsoft doesn't treat UAC as a security boundary, and says so. Locking your own folders down wouldn't make it one.

So PATHology lists this **UAC exposure** as a note, never as a problem, and keeps it apart from the cases that do cross a boundary: folders *other* users can write, folders in the machine PATH that SYSTEM searches, and folders sandboxed programs can write.

## Sandboxed programs

Browsers, PDF readers and Store apps run their riskiest code in a sandbox at **Low integrity**. Low-integrity code can't write to anything labelled Medium, and an ordinary folder counts as Medium, so it can't plant files in your PATH. A folder labelled Low (everything under `AppData\LocalLow`, say) is different: a compromised sandbox could plant a file there that then runs outside the sandbox, as you. That *is* a boundary, so PATHology reports it as a problem.

## The baseline

Windows puts `%LOCALAPPDATA%\Microsoft\WindowsApps` (where app execution aliases such as `winget` and `python` live) in every user's PATH. On its own that's how Windows ships, so PATHology notes it rather than marking you down for it.

## Mapped drives and elevation

Drive letters you map are per logon session. Your elevated session is a different one, so it doesn't see your mapped drives unless the `EnableLinkedConnections` policy is set, and SYSTEM never does. A PATH entry on a mapped drive simply vanishes for those programs, or worse, points at whatever that letter means to them.

## What helps

- Install tools for all users under `C:\Program Files`, which only administrators can write.
- Keep writable folders *after* the Windows ones on PATH.
- If you want elevation to be a real boundary, turn on **Administrator Protection** (Windows 11 24H2 and later): elevated programs then run as a separate, system-managed account with its own profile and PATH. PATHology notices and drops the UAC note.
- Or, for regular admin work, use a separate administrator account, so your everyday PATH never reaches elevated sessions at all.
