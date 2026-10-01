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
- [ ] Nav: Dashboard, *Entries* with System and User indented under it, Shadowing, History; Learn, Settings
      and About at the bottom. System and User's badges count the entries with a problem.
- [ ] The window, title bar and taskbar icon is the PATHology logo, crisp at small sizes.

### Dashboard
- [ ] Security **HIGH**, 9 high · 2 medium, "Fix 9 to bring it to Medium".
- [ ] Correctness **HIGH**, 2 high · 1 medium · 3 low, "Fix 2 to bring it to Medium".
- [ ] Hygiene **LOW**, 1 low, "Fix 1 to make it clean".
- [ ] *Things to fix* leads with SEC-05, "7 PATH folders inherit write access for every user from C:\", fix
      *Lock down the 7 folders…*. The three missing `C:\Android\android-sdk\…` folders are there (High), each
      with a removal. COR-01 names `%NVM_SYMLINK%`.
- [ ] Clicking one opens its entry on System or User, picked out, with that problem in the panel.
- [ ] *Worth knowing* has the UAC exposure (about 24 folders) and the length warning (the PATH is about 98% of
      2,047). Clicking one unfolds what to do; *Show the entry* and the Learn link go where they say.
- [ ] The summary line gives the scan time, the entry counts and the length; *What the scan looked at* unfolds.

### System and User
- [ ] System PATH: REG_EXPAND_SZ, 29 entries. User PATH: **REG_SZ**, 20 entries. Your user PATH really is
      stored as REG_SZ; there's no COR-02 only because it has no `%…%` in it.
- [ ] `C:\scripts`: the panel lists SEC-05/SEC-01/SEC-04 with the lock-down to stage; *What's there, and who can
      write to it* says a standard user can add files "through Authenticated Users".
- [ ] `C:\Windows\system32`: "Nothing wrong with this one."; its details show only you elevated and SYSTEM can write.
- [ ] `%NVM_SYMLINK%` row says the variable isn't expanded; the missing folders say "missing".
- [ ] The `…\nvm\\.nodejs` entry shows the doubled backslash highlighted.
- [ ] Each row shows the stored text; one using a `%VARIABLE%` has what it expands to beneath, in lighter text.
- [ ] Right-click a row: it's picked out, and the menu has the same actions as the panel.

### Shadowing
- [ ] `where` runs `C:\Windows\system32\where.exe`.
- [ ] `java` runs the first JDK and hides the second (the COR-07 note).
- [ ] `node` and `python` show their real winners and hidden copies.
- [ ] Windows commands: the C:\ group is listed under "Could be shadowed".

### Learn, Settings, About
- [ ] Each of the seven articles renders: headings, lists, the table in *REG_SZ and REG_EXPAND_SZ*, code blocks.
- [ ] Settings → Export a redacted snapshot: save it, open it, and confirm there's no username, PC name or
      script name in it (`<user>`, `<cmd-n>.bat` and `<var-n>` instead).
- [ ] Settings → untick *Scan when PATHology opens*, restart: the Dashboard says "Not scanned yet" until Scan is
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
- [ ] No UAC exposure problem on the Dashboard: a standard user has no elevated sessions for PATH to leak into.
- [ ] Security is still **HIGH**: the system PATH folders are writable by every user whoever looks.
      SEC-01's text calls it an **escalation to SYSTEM**, not a UAC bypass.
- [ ] *What the scan looked at*: the perspectives line still lists all four, and *you elevated* is the same as *you*.
- [ ] A system folder's details: *You* matches *A standard user*.
- [ ] The user PATH is that account's own (probably just WindowsApps), not yours.

Delete the account afterwards (Settings → Accounts → Other users → Remove).

---

## 3. Edge cases worth a minute
- [ ] With a USB stick or SD card reader on a PATH drive letter (if you have one): no "insert a disk" dialog;
      the entry shows "removable drive" or "drive not mounted".
- [ ] *Probe network paths* stays off: the Dashboard's *What the scan looked at* says none were probed. (This PC has no
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
- [ ] Nothing is staged to begin with: the pending bar says so, and *Review & apply* is disabled.
- [ ] *Stage the recommended fixes*: the bar shows where each rating would land; removed lines are struck
      through in place, and lines a fix changes say "fix staged: …". *Search the Windows folders first* (on the
      System PATH card) isn't among them.
- [ ] The C:\ lock-down shows on each folder it covers; unstaging it on one line unstages it on all of them.
- [ ] *Discard*: everything is unstaged.
- [ ] Delete a line: it stays struck through with *Put it back*, which restores it.
- [ ] Stage *Search the Windows folders first* → *Review & apply*: `where` (and anything else C:\scripts
      shadows) shows under "Commands that would run something else". Unstage it there.
- [ ] The VS Code / profile entries offer **Move … to your user PATH**, never a lock-down, and their "you can
      write it" problem offers the same move.
- [ ] Edit a row (Enter keeps it, Escape doesn't), move one up, move one to the other PATH: each shows on its line
      and in Review's diff, and *Undo my changes* / *Put it back* return them. Nothing has been written (a new
      `cmd`'s PATH is unchanged).

### A user-only change (no prompt)
- [ ] Stage only the user-PATH hygiene/empty-entry fix(es), then *Review & apply*. The admin line says "No UAC prompt".
- [ ] Apply → confirm. **No UAC prompt** appears. Status says Done; History has the record, APPLIED.
- [ ] A new `cmd` shows the tidied PATH. The re-scan after it: Hygiene improved, nothing is left staged, and the
      user PATH is still **REG_SZ** (User page).
- [ ] History → Undo → confirm: the user PATH is back exactly (compare with `user-env.reg`), and the record
      reads UNDONE with an "Undo of …" record above it.

### A machine change and a lock-down (one prompt)
- [ ] Stage one phantom removal (`C:\Android\…`) and *Lock down the 7 folders that inherit write access from
      C:\*. Review's admin line says one UAC prompt, for the system PATH and the folders.
- [ ] Apply → confirm → **decline** the UAC prompt: CANCELLED, nothing changed and both are still staged (a new
      `cmd`'s PATH is the same, and `C:\scripts` is still writable after a re-scan).
- [ ] Apply again → **accept**: exactly **one** prompt. Done; every step "done".
- [ ] Re-scan: SEC-05 and that SEC-03 are gone; the details of `C:\scripts` and `C:\programs\Python313\Scripts`
      say only you elevated and SYSTEM can add files. `icacls C:\scripts` shows inheritance off and Authenticated
      Users with (RX) only.
- [ ] `pip --version` still runs; `pip install` into it now needs an elevated prompt (that's the trade-off the
      fix's note mentions).
- [ ] History → Undo → accept the prompt: `icacls C:\scripts` shows inherited entries again, the phantom entry is
      back in the machine PATH, and a re-scan matches the start of this section.

### Moving a system entry to your user PATH (one prompt)
- [ ] Discard everything. System → pick the VS Code entry → *Move to your user PATH*: it's struck through on
      System ("moves to the user PATH") and first on User ("moved here from the system PATH"), and Review's admin
      line says the user PATH is written first.
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
