# PingTray

**v1.2** — 2026-07-17
**Author:** Andrew Reardon, andrewreardon@gmail.com

A standalone Windows system tray app that shows live ping latency as a
color-coded number in the notification area. Defaults to `1.1.1.1`, polled
every 3s; both are overridable via command-line args (see below).

## Files

- `src/PingTray.cs` — source (C#, WinForms `NotifyIcon`)
- `dist/PingTray.exe` — compiled, self-contained binary. Copy this folder to
  any Windows 10/11 machine and double-click it — no install, no dependencies
  beyond .NET Framework (present on every Windows 10/11 machine by default).
- `build.ps1` — rebuilds `dist/PingTray.exe` from `src/PingTray.cs` using the
  C# compiler bundled with .NET Framework (`csc.exe`). No SDK or internet
  connection needed. Run after editing the source, or after moving to a new
  machine if you don't trust the prebuilt exe (e.g. SmartScreen flags
  unsigned exes copied from removable media/network shares).
- `README.md` / `README.txt` — end-user docs (GitHub / plain-text copy for
  folder distribution). README.md also records the exact csc command and the
  SHA-256 of the committed exe — **update that hash whenever a rebuilt
  `dist/PingTray.exe` is committed** (`Get-FileHash dist\PingTray.exe`).

## How it works

- Pings the target host (default `1.1.1.1`) on the configured interval
  (default 3s) via `System.Net.NetworkInformation.Ping`, with a 2s timeout
  per attempt (`PingTimeoutMs`). A single missed reply is common on flaky
  links (4G) and isn't treated as offline — `_consecutiveFailures` must
  reach `FailureThreshold` (3) before the icon flips to the offline
  indicator. Below that threshold the icon keeps showing the last
  known-good reading (`_lastGoodMs`); only the tooltip reflects the
  in-progress failures (`no response 1/3 ...`). A single success resets the
  counter immediately. Worst-case detection latency is roughly
  `FailureThreshold * IntervalMs` (~9-11s at the defaults). Retargeting via
  the Settings dialog resets both `_consecutiveFailures` and `_lastGoodMs`
  so stale state from the old target doesn't leak into the new one.
- Renders the latency as text onto a 64x64 bitmap, converts it to an icon via
  `Bitmap.GetHicon()`, and sets it as the `NotifyIcon`'s icon. Windows then
  scales it down into the actual tray slot (~16-24px depending on display
  scaling) — this is a fixed OS-level constraint, not something the app
  controls, so digit "bigness" is maximized by filling as much of the source
  canvas as possible.
- Old icon handles are explicitly destroyed via `DestroyIcon` (P/Invoke) —
  `Icon.Dispose()` alone does *not* release HICONs created by `GetHicon()`,
  which would otherwise leak GDI handles on a long-running always-on app.
- A named mutex (`Global\PingTray_SingleInstance_<target>`) prevents two
  instances watching the same target, while allowing separate instances for
  different targets to run side by side.
- `NotifyIcon.Text` throws `ArgumentOutOfRangeException` if set to a string
  of 64+ characters. Since the target host is user-editable (Settings
  dialog / `--target`), tooltip length can't be budgeted by hand at each
  call site — some `IPStatus` values alone (e.g.
  `DestinationNetworkUnreachable`) are long enough to blow the limit once
  combined with a hostname. All tooltip writes go through `SetTooltip()`,
  which truncates defensively rather than trusting the caller.

## Color coding (icon color + tooltip range legend)

| Range        | Color  |
|--------------|--------|
| < 50 ms      | Green  |
| 50–150 ms    | Orange |
| ≥ 150 ms     | Red    |
| Offline (after 3 consecutive failures) | Black "0" |

Hovering the tray icon shows the exact ms value plus the same range legend,
e.g. `Ping to 1.1.1.1: 23 ms (<50 / 50-150 / ≥150)`. The legend is omitted
from the offline/error/retry tooltip variants to stay under the 63-char
limit regardless of hostname/status length (see `SetTooltip()` above).

## Settings dialog

Left-clicking the tray icon (or right-click → **Settings...**) opens a
`Form` built entirely in code (no designer file) with two fields — target
host and interval in seconds. On **OK**: validates both fields, updates the
running `Target`/`IntervalMs`, applies the new interval to `_timer`,
triggers an immediate `UpdatePing()`, and persists via `SaveSettings()`.

### Persistence model

Settings are saved to `settings-<sanitized-target>.ini` next to the exe,
where `<sanitized-target>` is `ProfileKey` — the target this **instance was
launched with** (from `--target=`, or the `1.1.1.1` default), captured once
at startup *before* any dialog edits. This is what makes multi-instance
monitoring work cleanly:

- Each launch config (a distinct `--target=` shortcut, or the no-args
  default) maps to its own `.ini` file and its own persisted settings.
- Retargeting an instance via the Settings dialog changes what it *watches*
  and *saves to*, but `ConfigPath` stays pinned to the original
  `ProfileKey` — so instance A's saved file never gets overwritten by
  instance B's edits, even if you point A at the same host B is already
  watching.
- Known edge case: the single-instance mutex is created from `Target`
  *before* the dialog can change it, so retargeting an instance at runtime
  doesn't update its mutex — a new instance launched afterward with
  `--target=` matching the *new* (dialog-set) value won't be blocked by it.
  Not handled; low-impact since it only affects the rare case of
  retargeting one running instance to collide with another instance you
  then launch fresh.

`.ini` format is plain `key=value` lines (`target=`, `interval=` in
seconds), hand-editable, `#`-comment lines ignored, malformed/missing files
silently fall back to whatever `Target`/`IntervalMs` already resolved to
from args/defaults.

## Running

Just double-click `dist\PingTray.exe`. Right-click the tray icon for
**Exit**.

New tray icons are hidden by Windows 11 by default — check the "^" overflow
chevron near the clock, or go to **Settings → Personalization → Taskbar →
Other system tray icons** and toggle it to always show.

### Command-line options

```
PingTray.exe [--target=<host>] [--interval=<seconds>]
```

- `--target=1.0.0.1` — ping a different host instead of `1.1.1.1`.
- `--interval=10` — poll every 10 seconds instead of the 3s default.

Unrecognized/omitted args fall back to the defaults. Different targets get
independent single-instance locks, so you can run multiple copies side by
side (e.g. one for `1.1.1.1`, another for your gateway) without them
fighting over the same instance lock — running the *same* target twice is
still blocked.

**Precedence:** `--target=`/`--interval=` pick which profile's `.ini` file
gets loaded (see Settings dialog / Persistence model below), but if that
file already has saved values (e.g. from a previous Settings-dialog edit
under that same profile), the file's values win over the CLI args for the
actual live `Target`/`IntervalMs`. In practice this only matters if you
edit a shortcut's `--target=` after already having run it once and saved
different settings through the dialog — delete the corresponding
`settings-*.ini` to reset a profile back to its CLI/default values.

To pass args to the autostart copy, edit the Startup shortcut's Target field
(see below) and append the flags there.

## Autostart

A shortcut at
`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\PingTray.lnk`
launches `PingTray.exe` (no args, so default target/interval) on login. To
change args, remove/edit it (Win+R → `shell:startup`). To disable autostart,
just delete that shortcut — it doesn't touch `PingTray.exe` itself.

This shortcut is machine-local (created by `build.ps1`'s companion setup, not
part of this folder) — after copying `PingTray` to a new machine, re-create
it there if you want autostart on that machine too:

```powershell
$startupDir = [Environment]::GetFolderPath('Startup')
$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut((Join-Path $startupDir "PingTray.lnk"))
$shortcut.TargetPath = "C:\ajr\PingTray\dist\PingTray.exe"   # update path if moved
$shortcut.WorkingDirectory = "C:\ajr\PingTray\dist"
$shortcut.Save()
```

## Not yet set up

- **Code signing.** The exe is unsigned. If Windows SmartScreen or a
  network/USB transfer flags it (Mark-of-the-Web), right-click → Properties
  → Unblock, or just rebuild locally with `build.ps1`, which sidesteps trust
  prompts entirely since it compiles on the machine it runs on.
