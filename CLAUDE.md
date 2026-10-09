# PingTray

**v1.4** — 2026-10-09
**Author:** Andrew Reardon, andrewreardon@gmail.com

A standalone Windows system tray app that shows live ping latency as a
color-coded number in the notification area. Defaults to `1.1.1.1`, polled
every 5s; both are overridable via command-line args (see below).

## Files

- `src/PingTray.cs` — source (C#, WinForms `NotifyIcon`)
- `src/PingTray.ico` — icon embedded into the exe via `/win32icon`
  (Explorer/taskbar/Task Manager identity, and the tray placeholder until
  the first ping paints a number). Version resources (Properties → Details)
  come from the `[assembly: ...]` attributes at the top of `PingTray.cs`,
  which derive from the same `Version` const used by `-v` and About.
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
  (default 5s) via `System.Net.NetworkInformation.Ping.SendPingAsync`, with
  a 2s timeout per attempt (`PingTimeoutMs`). The await resumes on the UI
  thread via the WinForms `SynchronizationContext`, so a timing-out attempt
  never blocks the message pump (menu/tooltip stay responsive while the
  target is down). `_pingInFlight` skips a tick if the previous attempt is
  still pending — `Ping` throws if a second send starts while one is in
  flight — and `UpdatePing` snapshots `Target` at send time so a reply that
  lands just after a Settings-dialog retarget is labeled with the host that
  was actually pinged. A single missed reply is common on flaky
  links (4G) and isn't treated as offline — `_consecutiveFailures` must
  reach `FailureThreshold` (3) before the icon flips to the offline
  indicator. Below that threshold the icon keeps showing the last
  known-good reading (`_lastGoodMs`); only the tooltip reflects the
  in-progress failures (`no response 1/3 ...`). A single success resets the
  counter immediately. Worst-case detection latency is roughly
  `FailureThreshold * IntervalMs` (~15-17s at the defaults). Retargeting via
  the Settings dialog resets both `_consecutiveFailures` and `_lastGoodMs`
  so stale state from the old target doesn't leak into the new one.
- Renders the latency as outlined text onto a 32x32 bitmap (`RenderIcon` /
  `DrawFit`, the same drawing as HeatTray: the number is a `GraphicsPath`
  outline scaled to fill the canvas, with a thin halo), converts it to an
  icon via `Bitmap.GetHicon()`, and sets it as the `NotifyIcon`'s icon.
  Windows then scales it down into the actual tray slot (~16-24px depending
  on display scaling) — this is a fixed OS-level constraint, not something
  the app controls, so digit "bigness" is maximized by filling as much of
  the source canvas as possible. `TightText` places each character by its
  ink width (a narrow "1" takes little room) and digits are never stretched
  beyond `MinSqueeze` (0.85) of their natural shape, so numbers are big but
  not elongated. Before this (up to the released v1.3) it called `DrawString`
  at a font size chosen by digit count on a 64x64 canvas: thin digits only
  5-13 px tall at 125% scaling, no outline, and the same colours on every
  taskbar. Measured at 125%: 1-2 digit numbers 15-19 px tall now (outline
  included), 3 digits 11-15 px, "999+" 8 px.
  Known and left alone (Fable review of this change): the canvas is 32 px
  like HeatTray's, so at 250% scaling and above (40-48 px tray slots) the
  icon is upscaled and a little soft where the old 64 px canvas was
  downscaled (exact at 200%, downscaled at 100-150%); the fitted path sits
  half a pixel off the box (GDI+ default pixel offset) so the halo is
  clipped by 0.5 px on the right and bottom - invisible at 20 px; and
  `new FontFamily("Segoe UI")` throws if the font is missing (the old
  `new Font(...)` fell back silently) - unreachable on Windows 10/11.
  Release note: v1.4 (2026-10-09) is this icon drawing. The `v1.3` tag and
  its release zip hold the old exe; a GitHub release for v1.4 needs the new
  `dist/PingTray.exe` zipped (the README's "latest release zip" link points
  at whichever release is newest).
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

The colours follow the Windows taskbar theme: `IsLightTaskbar()` reads
`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\SystemUsesLightTheme`
(the system theme, dark when missing) on every redraw, i.e. every ping. Dark
taskbar: LimeGreen / Orange / Red with a dark outline; light taskbar: darker
shades (0,130,0 / 205,100,0 / 200,0,0) with a light outline (the old colours
were about 2:1 on white for green and orange, 4:1 for red). The offline "0"
is black on both but always gets a bold (3.6 px) light outline: plain black
on a dark taskbar was nearly invisible (about 1.4:1 on a maroon accent
taskbar). `RenderIcon(text, tone,
light)` takes `light` explicitly as the test seam - do not flip the registry
value to test, render onto light backgrounds instead.

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

There is a single saved-settings file, `settings.ini`, next to the exe,
written only when **OK** is pressed in the Settings dialog. Precedence is
strictly "command line wins":

- **No `-t`/`-i` options** (double-click, autostart): `settings.ini` is
  loaded, so the app resumes whatever it was monitoring when settings were
  last saved. Missing/malformed file → built-in defaults (`1.1.1.1`, 5s).
- **Any explicit `-t`/`-i` option**: `settings.ini` is ignored entirely —
  the run is built-in defaults plus the given flags (`_targetFromCli` /
  `_intervalFromCli` in `ParseArgs`). CLI flags are never written to
  `settings.ini`, so ad-hoc runs don't pollute the saved state.
- Multi-instance note: all instances share the one `settings.ini`; the
  last dialog **OK** wins. Instances launched with explicit flags never
  read or clobber it unless you save from their Settings dialog.
- Known edge case: the single-instance mutex is created from `Target`
  *before* the dialog can change it, so retargeting an instance at runtime
  doesn't update its mutex — a new instance launched afterward with a
  target matching the *new* (dialog-set) value won't be blocked by it.
  Not handled; low-impact.

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
PingTray.exe [-t <host> | --target=<host>] [-i <sec> | --interval=<seconds>]
             [-h | --help] [-v | --version]
```

- `-t 1.0.0.1` / `--target=1.0.0.1` — ping a different host instead of
  `1.1.1.1` (`-t=host` also accepted).
- `-i 10` / `--interval=10` — poll every 10 seconds instead of the 5s
  default (`-i=10` also accepted).
- `-h` / `--help` / `-?` / `/?` — print usage and exit.
- `-v` / `--version` — print version/author info and exit.

The exe is `/target:winexe` (no console), so `-h`/`-v` output goes through
`PrintOrShow()`: it calls `AttachConsole(ATTACH_PARENT_PROCESS)` to write
back into the invoking terminal (or uses inherited stdout when piped or
redirected), falling back to a `MessageBox` only when there's no console at
all (e.g. flag set on a double-clicked shortcut). Because the shell doesn't
wait for a GUI-subsystem exe, the prompt prints *before* our output and the
console would then sit on a bare line that looks hung — so after writing to
an attached interactive console (stdout is `FILE_TYPE_CHAR`, i.e. not piped
or redirected), `PrintOrShow()` injects one Enter key event into the console
input buffer (`WriteConsoleInput`) to make the shell redraw its prompt. A
failed console/pipe write (reader closed the pipe early) exits quietly
instead of falling back to the dialog. Launching a duplicate
instance (same target already running) shows an "already running" info
dialog instead of exiting silently. Version/author constants (`Version`,
`VersionDate`, `Author`, `AuthorEmail`) live at the top of `PingTray.cs`
and feed `-v`, `-h`, and the tray menu's **About PingTray...** dialog —
bump `Version`/`VersionDate` on each release alongside the README headers.

Unrecognized/omitted args fall back to the defaults. Different targets get
independent single-instance locks, so you can run multiple copies side by
side (e.g. one for `1.1.1.1`, another for your gateway) without them
fighting over the same instance lock — running the *same* target twice is
still blocked.

**Precedence:** the command line always wins. Launched with no options,
the app loads `settings.ini` (whatever was last saved via the Settings
dialog); launched with any explicit `-t`/`-i`, it ignores `settings.ini`
entirely and runs with built-in defaults plus the given flags. CLI flags
are never persisted (see Persistence model below).

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
