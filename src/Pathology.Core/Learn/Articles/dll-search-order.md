When a program loads a DLL by name alone (`LoadLibrary("helper.dll")`, or an import it can't find beside itself), Windows has to work out *which* `helper.dll`. After the special cases (DLLs already loaded, the **Known DLLs** list, side-by-side manifests), it searches folders in this order:

1. The folder the program's `.exe` is in
2. `C:\Windows\System32`
3. `C:\Windows\System` (the old 16-bit folder)
4. `C:\Windows`
5. The current directory
6. **Every folder on PATH, in order**

PATH comes last, so it only matters when a DLL isn't found anywhere earlier. That happens more often than you'd think: optional plug-ins, a DLL from an uninstalled product, a dependency that only exists on some Windows versions. Each of those lookups falls all the way through to PATH.

## Why that makes PATH a security boundary

Whatever process asks, the DLL it finds is loaded into *that* process and runs with *its* rights. Windows services run as SYSTEM, and they search the **machine** PATH. So if any folder on the machine PATH is writable by an ordinary user, that user can drop in a DLL a service looks for and fails to find, and their code runs as SYSTEM the next time the service starts.

This is one of the most common local privilege escalations on Windows. It needs no exploit, only a folder with the wrong permissions. It's why PATHology judges every folder from SYSTEM's side as well as yours, and why a writable machine PATH folder is rated **High** even if nothing in it is ever run directly.

## What protects you

- Well-written programs call `SetDefaultDllDirectories` or load with `LOAD_LIBRARY_SEARCH_SYSTEM32`, which takes PATH out of the search entirely. You can't tell from outside which programs do.
- **Known DLLs** (the core system DLLs) are never searched for at all.
- Folders under `C:\Windows` and `C:\Program Files` are writable only by administrators out of the box.

## The fix

Keep every machine PATH folder writable by administrators only. A folder that has to be on PATH and has to be writable (a scratch tools folder) belongs in your **user** PATH, where only your own programs search it.
