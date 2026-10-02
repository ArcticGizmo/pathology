There isn't one PATH. There are two stored values and many copies.

## The stored values

- The **machine PATH**: `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment`, value `Path`. It applies to everyone, including services.
- The **user PATH**: `HKCU\Environment`, value `Path`. It applies to you.

## Building the environment

When Windows builds the environment for a new logon (and when Explorer refreshes it), it works through the variables in order:

1. **System variables**, expanded using only what's been defined so far.
2. **Your variables**, expanded with the system ones (and your profile's, such as `%USERPROFILE%`) available.
3. The per-logon **volatile** values (`HKCU\Volatile Environment`).

PATH gets special treatment: your user PATH is **appended** to the machine PATH rather than replacing it. Machine entries always come first.

Two consequences:

- A machine PATH entry that uses a variable only *you* define (`%JAVA_HOME%` set in your user variables) stays as literal text, because system variables are expanded before yours exist. The entry is dead.
- On recent builds, a user entry that exactly repeats a machine entry may be dropped from the combined PATH. That's why PATHology compares the registry with what a new process actually gets.

## Every program has a copy

A process gets a **copy** of its parent's environment when it starts, and keeps that copy. Most programs are started by Explorer, so they get Explorer's copy.

When PATH is changed in the registry, nothing already running notices. The Environment Variables dialog broadcasts a `WM_SETTINGCHANGE` message so Explorer reloads, but open terminals, IDEs and services keep their old PATH until they're restarted. Signing out and back in refreshes everything.

## How long it can be

Any environment variable can hold at most **32,767** characters. Long before that, at **2,047**, older programs, some installers and the classic Environment Variables dialog start truncating or failing. A truncated PATH written back loses whatever was at the end, which is often the newest entries.
