# Manual test matrix (M5)

A scan is **read-only**: it reads the registry and each folder's permissions and writes nothing. Sections 1–3
are safe on any machine and change nothing. **Section 4 (M6) does change your PATH and folder permissions**,
on purpose, and undoes them again. Read it through before starting.

Run the app from source (`dotnet run --project src/Pathology.App`: a Debug build, pink `- DEV` badge, separate
settings store), or the published exe (see [packaging.md](packaging.md)) to test exactly what ships.

**Throughout, watch for things that must never happen:** a UAC prompt, an "insert a disk" dialog, a sign-in
prompt for a network share, or any new file or folder in a PATH folder.

The expected values below come from this dev PC's snapshot of 2026-10-01
(`tests/Pathology.Tests/Fixtures/dev-machine.redacted.json`). If your PATH has changed since, the counts will
too. What matters is that each count traces back to a real cause.

---

## 1. Administrator with UAC on (the dev PC as it is)

This also covers the planned *"writable `C:\Tools` in the machine PATH"* case: `C:\scripts`,
`C:\scripts\aliases`, `C:\programs\Python313` and `…\Scripts` are ahead of System32 and inherit write access for
every user from `C:\`.

### Launch
- [ ] The scan starts by itself, and the checklist ticks through its six steps.
- [ ] Nav badges: Findings shows the number of High problems (11 here), and Entries shows the entry count (49).
- [ ] The window, title bar and taskbar icon is the PATHology logo, crisp at small sizes.

### Health
- [ ] Security **HIGH**, 9 high · 2 medium, "Fix 9 to bring it to Medium".
- [ ] Correctness **HIGH**, 2 high · 1 medium · 3 low, "Fix 2 to bring it to Medium".
- [ ] Hygiene **LOW**, 1 low, "Fix 1 to make it clean". 12 notes.
- [ ] UAC exposure **MEDIUM**: about 24 folders you can write that elevated programs also search.
- [ ] Length headroom: the 2,047 bar is red at about 98%.
- [ ] Clicking a problem row opens Findings on it; "All N … problems" opens that category.

### Findings
- [ ] The first group is SEC-05, "7 PATH folders inherit write access for every user from C:\", with SEC-01 and
      SEC-04 listed under "Same cause, also found by".
- [ ] Its evidence names *Authenticated Users: modify (inherited)*.
- [ ] The three missing `C:\Android\android-sdk\…` folders are SEC-03 (High), each with its COR-05 under it.
- [ ] COR-01 names `%NVM_SYMLINK%` as defined only in your user environment.
- [ ] Filters: "Notes" shows the 12 notes; "Hygiene" shows only HYG-03; "User PATH" narrows correctly.
- [ ] Clicking an entry in the detail pane opens it on Entries; the Learn link opens the right article.
- [ ] Copy details pastes readable plain text into Notepad.

### Entries
- [ ] Machine PATH: REG_EXPAND_SZ, 29 entries. User PATH: **REG_SZ**, 20 entries. Your user PATH really is
      stored as REG_SZ; there's no COR-02 only because it has no `%…%` in it.
- [ ] `C:\scripts`: red square under *Std*, orange under *You*; detail says "through Authenticated Users".
- [ ] `C:\Windows\system32`: hollow under *You* and *Std*, grey under *Elev.* and *SYSTEM*.
- [ ] `%NVM_SYMLINK%` row says the variable isn't expanded; the missing folders say "missing".
- [ ] The `…\nvm\\.nodejs` entry shows the doubled backslash highlighted; toggling *Highlight defects* clears it.
- [ ] Toggling *Expanded* shows the stored text.

### Shadowing
- [ ] `where` runs `C:\Windows\system32\where.exe`.
- [ ] `java` runs the first JDK and hides the second (the COR-07 note).
- [ ] `node` and `python` show their real winners and hidden copies.
- [ ] Windows commands: the C:\ group is listed under "Could be shadowed".

### Learn, Settings, About
- [ ] Each of the seven articles renders: headings, lists, the table in *REG_SZ and REG_EXPAND_SZ*, code blocks.
- [ ] Settings → Export a redacted snapshot: save it, open it, and confirm there's no username, PC name or
      script name in it (`<user>`, `<cmd-n>.bat` and `<var-n>` instead).
- [ ] Settings → untick *Scan when PATHology opens*, restart: Health says "Not scanned yet" until Scan is
      clicked. Tick it again.
- [ ] Re-scan while on any page: the checklist shows, then the page refreshes and keeps its selection.
- [ ] About: version, Check for updates ("wasn't installed by the installer" from source), View changelog.

---

## 2. Standard user

Needs an account that isn't an administrator. A temporary local one is easiest:

1. Settings → Accounts → Other users → Add account → "I don't have this person's sign-in information" →
   "Add a user without a Microsoft account". Leave it a **standard** user.
2. Publish the app (`dotnet publish …`, see packaging.md) to a folder that account can read, e.g. `C:\Temp\pathology`.
3. Run it as that account: `runas /user:%COMPUTERNAME%\<account> C:\Temp\pathology\pathology.exe`.
   (Or sign in as the account.) Its settings land in that account's own `%LOCALAPPDATA%`.

Expected:
- [ ] UAC exposure: **Not applicable**: "You're a standard user…".
- [ ] Security is still **HIGH**: the machine PATH folders are writable by every user whoever looks.
      SEC-01's text calls it an **escalation to SYSTEM**, not a UAC bypass.
- [ ] Judged as: the perspectives line still lists all four, and *you elevated* is the same as *you*.
- [ ] Entries' *You* column now matches *Std* for the machine folders.
- [ ] The user PATH is that account's own (probably just WindowsApps), not yours.

Delete the account afterwards (Settings → Accounts → Other users → Remove).

---

## 3. Edge cases worth a minute
- [ ] With a USB stick or SD card reader on a PATH drive letter (if you have one): no "insert a disk" dialog;
      the entry shows "removable drive" or "drive not mounted".
- [ ] *Probe network paths* stays off: Health's "Network paths" line says none were probed. (This PC has no
      network entries, so the opt-in path isn't exercised here.)
- [ ] Resize the window to its minimum (820×520): nothing overlaps, and the panes scroll.

---

## 4. Fix and undo (M6): this one writes

Run from source (a Debug build keeps its history in `PATHology Data (Dev)`). Before you start, keep a copy you
can restore without PATHology. From a normal prompt:
`reg export "HKCU\Environment" %USERPROFILE%\Desktop\user-env.reg`, and from an elevated one,
`reg export "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" %USERPROFILE%\Desktop\machine-env.reg`.
Have a `cmd` window open throughout, and run `echo %PATH%` in a **new** one after each step.

### Dry run
- [ ] Fix lists the fixes, recommended ones ticked; the ratings show where each category would land.
- [ ] Untick everything: "What changes" says nothing yet, and Apply is disabled.
- [ ] Tick *Search the Windows folders first*: `where` (and anything else C:\scripts shadows) shows under
      "Commands that would run something else". Untick it again.
- [ ] The VS Code / profile entries offer **Move … to your user PATH**, never a lock-down.
- [ ] Entries → pick `C:\scripts` → *Change it on Fix*: the row is highlighted in the editor.
- [ ] Edit a row, move one up, move one to the other scope, add `C:\Temp\nothing`: each shows in the diff, and
      "Reset my edits" puts them back. Nothing has been written (a new `cmd`'s PATH is unchanged).

### A user-only change (no prompt)
- [ ] Tick only the user-PATH hygiene/empty-entry fix(es). The admin line says "No UAC prompt".
- [ ] Apply → confirm. **No UAC prompt** appears. Status says Done; History has the record, APPLIED.
- [ ] A new `cmd` shows the tidied PATH. Re-scan: Hygiene improved, and the user PATH is still **REG_SZ**
      (Entries).
- [ ] History → Undo → confirm: the user PATH is back exactly (compare with `user-env.reg`), and the record
      reads UNDONE with an "Undo of …" record above it.

### A machine change and a lock-down (one prompt)
- [ ] Tick one phantom removal (`C:\Android\…`) and *Lock down the 7 folders that inherit write access from
      C:\*. The admin line says one UAC prompt, for the machine PATH and the folders.
- [ ] Apply → confirm → **decline** the UAC prompt: CANCELLED, nothing changed (a new `cmd`'s PATH is the same,
      and Entries still shows `C:\scripts` writable after a re-scan).
- [ ] Apply again → **accept**: exactly **one** prompt. Done; every step "done".
- [ ] Re-scan: SEC-05 and that SEC-03 are gone; `C:\scripts` and `C:\programs\Python313\Scripts` show hollow
      under *Std* and *You*. `icacls C:\scripts` shows inheritance off and Authenticated Users with (RX) only.
- [ ] `pip --version` still runs; `pip install` into it now needs an elevated prompt (that's the trade-off the
      fix's note mentions).
- [ ] History → Undo → accept the prompt: `icacls C:\scripts` shows inherited entries again, the phantom entry is
      back in the machine PATH, and a re-scan matches the start of this section.

### Moving a machine entry to your user PATH (one prompt)
- [ ] Untick everything on Fix. Entries → pick a machine entry (the VS Code one) → *Move to your user PATH*:
      Fix opens with it highlighted at the top of the user PATH, and the admin line says the user PATH is
      written first.
- [ ] Apply → confirm → **decline** the prompt: CANCELLED, and the user PATH is back exactly (compare with
      `user-env.reg`); the entry is still in the machine PATH.
- [ ] Apply again → **accept**: History lists *User PATH* (written before the machine PATH) above *Machine PATH*,
      both done. A new `cmd`'s PATH has the entry once, in its new place.
- [ ] History → Undo → accept: the entry is back in the machine PATH and gone from the user PATH.

### Things that must never happen
- [ ] A UAC prompt for a user-only change, or more than one prompt for one apply.
- [ ] Any file appearing in `PATHology Data (Dev)\pending` that outlives an apply.
- [ ] A PATH value changing kind (REG_SZ ↔ REG_EXPAND_SZ) without the fix that says so.
- [ ] Anything changed after declining the prompt.
