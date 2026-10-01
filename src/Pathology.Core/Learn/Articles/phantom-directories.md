A PATH entry for a folder that doesn't exist looks harmless: Windows skips it and moves on. It's only harmless while nobody can **create** it.

## How they appear

- An uninstaller removes a program's folder but leaves its PATH entry behind.
- A tool adds its folder to PATH before (or instead of) creating it.
- An entry points at a drive letter that isn't there right now: a USB stick, a removed disk, a card reader.

## Why they matter

Whoever creates the missing folder decides what's in it. On a default Windows install, **Authenticated Users can create folders at the root of `C:\`**. So if the machine PATH contains `C:\OldApp\bin` and `C:\OldApp` is gone, any user can create `C:\OldApp\bin` and fill it with DLLs and commands. Services search the machine PATH as SYSTEM, so that user's files load as SYSTEM (see *DLL search order*).

A missing drive letter is similar. Whoever can mount a drive with that letter, by plugging in a USB stick for example, controls what's on it.

PATHology checks the **nearest folder that does exist** above each missing entry, and asks who could create the rest. A dead entry that no one but administrators could create is a tidiness problem (Low). One that any user could create is a security problem (High), and the two are reported as one.

## The fix

Remove the entry. If something genuinely needs it, create the folder yourself as an administrator, so it inherits locked-down permissions instead of whatever an attacker chooses.
