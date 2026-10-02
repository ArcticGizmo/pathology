A registry value has a **type**, and string values come in two kinds:

| Type | Registry Editor calls it | `%VAR%` references |
|---|---|---|
| `REG_EXPAND_SZ` | Expandable String Value | expanded when the environment is built |
| `REG_SZ` | String Value | kept as literal text |

Windows stores PATH as `REG_EXPAND_SZ`, because its own entries are written as `%SystemRoot%\system32`.

## When the type is wrong

If PATH has been rewritten as `REG_SZ`, every `%...%` in it stays literal. `%SystemRoot%\system32` isn't a folder, so the entry is dead, and Windows' own folders quietly drop off your PATH. Commands such as `where`, `ping` or `powershell` stop resolving in new windows, while old windows (which still hold the earlier copy) keep working. That makes it baffling to track down.

It usually happens when a tool writes PATH back as a plain string: a script using `reg add` without `/t REG_EXPAND_SZ`, an installer, or code that reads PATH, edits it and writes it with the default string type.

## The fix

Store the value as `REG_EXPAND_SZ` again, with the same text. In Registry Editor you can't change a value's type in place. Copy the data, delete the value, create an **Expandable String Value** named `Path`, and paste the data back. Then sign out and in again, so everything picks it up.

The other direction is also worth knowing: a `REG_EXPAND_SZ` value with no `%` in it is harmless. Nothing needs expanding, so it behaves exactly like `REG_SZ`.
