Search for "add to PATH on Windows" and you'll find this:

```bat
setx PATH "%PATH%;C:\NewTool"
```

It seems to work. It also quietly damages your PATH in three ways.

## 1. It writes the combined PATH into one scope

`%PATH%` in your shell is the **machine and user PATH joined together**. Without `/M`, `setx` writes that whole joined string into your **user** PATH. Every machine entry is now in your user PATH as well, duplicated, and growing each time someone runs the line again.

## 2. It freezes the variables

`%PATH%` in your shell is already **expanded**. `%SystemRoot%\system32` is now written as `C:\Windows\system32`, and `%JAVA_HOME%\bin` as whichever JDK was current that day. Change `JAVA_HOME` later and PATH doesn't follow.

## 3. It truncates at 1,024 characters

`setx` cuts any value longer than **1,024 characters** down to 1,024. It prints a one-line warning that's easy to miss in a script. Everything past that point is gone, often with the last entry cut in half. PATHology flags a PATH that's *exactly* 1,024 characters for this reason.

## What to do instead

- Use the **Environment Variables** dialog: Start → "Edit environment variables for your account" (or the system ones, as an administrator). It edits one scope at a time, keeps `%VAR%` references, and tells running programs about the change.
- In a script, read the **one** stored value you mean to change, from the registry, unexpanded. Append to it and write it back with the same value type (see *REG_SZ and REG_EXPAND_SZ*).
- If you're cleaning up after `setx`, compare your user PATH with your machine PATH. Any entry that appears in both is probably a copy.
