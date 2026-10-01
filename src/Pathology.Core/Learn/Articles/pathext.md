Type `where` at a prompt and something has to decide which file that means. In **cmd**:

1. The current directory is searched first (unless the `NoDefaultCurrentDirectoryInExePath` environment variable is set).
2. Then each folder on PATH, **in PATH order**.
3. Within each folder, each extension in `PATHEXT` is tried **in PATHEXT order**: by default `.COM`, `.EXE`, `.BAT`, `.CMD`, `.VBS`, `.VBE`, `.JS`, `.JSE`, `.WSF`, `.WSH`, `.MSC`.

The first match wins. Folder order beats extension order, so:

- `where.bat` in a folder *earlier* on PATH beats `where.exe` in System32.
- In the *same* folder, `where.com` beats `where.exe`.
- A name you type with an extension (`where.exe`) only matches that exact file.

That's why PATHology cares about **writable folders that come before System32**: anyone who can write there can put a `net.bat` or `where.com` in your way, and every script that calls those commands runs theirs instead. The Shadowing page resolves names exactly this way.

## PowerShell is different

PowerShell looks for **aliases, functions and cmdlets first**, so `dir` or `Get-Item` can't be shadowed by a file. After those, it searches PATH for programs much as cmd does, and it also finds `.ps1` scripts on PATH. It does **not** run things from the current directory unless you type `.\name`.

## Programs starting programs

When a program calls `CreateProcess` with a bare name, the rules change again: it searches the program's own folder, the current directory, then System32, the 16-bit System folder and the Windows folder, and only then PATH. It also adds only `.exe`, ignoring PATHEXT. So a planted `net.bat` beats `net.exe` in a shell, but not when a program launches `net` directly.

Shells and scripts are where shadowing bites. A writable folder anywhere on PATH is still a DLL-planting risk (see *DLL search order*).
