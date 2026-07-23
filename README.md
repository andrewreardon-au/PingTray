# Ping Tray

**v1.2** — 2026-07-17
**Author:** Andrew Reardon, andrewreardon@gmail.com

Shows your network latency as a number in the system tray, right next to the clock.

## What the number means

| Color | Meaning |
|---|---|
| 🟢 Green | Good — under 50ms |
| 🟠 Orange | OK — 50 to 150ms |
| 🔴 Red | Slow — 150ms or higher |
| ⚫ Black "0" | Offline — no response |

Hover over the icon to see the exact number. It checks every 3 seconds by default.

## What's in this repo

| Path | What it is |
|---|---|
| `src/PingTray.cs` | The entire app — one plain C# file, no dependencies beyond .NET Framework |
| `dist/PingTray.exe` | Prebuilt, ready-to-run binary (see [How dist/PingTray.exe was built](#how-distpingtrayexe-was-built)) |
| `build.ps1` | Rebuilds `dist/PingTray.exe` from source on your own machine |

No installer needed: **Code → Download ZIP**, extract, and run.

A single missed reply doesn't flip it to offline — that's normal on flaky connections like 4G. It takes **3 misses in a row** before it shows black "0"; until then it keeps showing your last good reading, and hovering shows something like `no response 1/3 (last: 23ms)` so you know it's currently struggling without the number jumping around.

## Getting started

1. Double-click `dist\PingTray.exe`.
2. Don't see it? Click the small **^** arrow near the clock to show hidden tray icons.
3. To close it, right-click the icon → **Exit**.

## Changing what it watches

Click the tray icon (or right-click → **Settings...**) to open a small settings box where you can change the target host and how often it checks. Hit **OK** and it applies immediately — no need to restart, and your choice is remembered for next time you open Ping Tray.

## Keeping it always visible

By default, Windows may tuck new tray icons away under the **^** hidden-icons arrow instead of showing them next to the clock. To pin it visibly:

1. Right-click the taskbar → **Taskbar settings**.
2. Click **Other system tray icons**.
3. Find **PingTray** in the list and toggle it **On**.

## Starting automatically on login

To have Ping Tray launch on its own every time you sign in:

1. Right-click `dist\PingTray.exe` → **Create shortcut**.
2. Press `Win + R`, type `shell:startup`, and hit Enter — this opens your Startup folder.
3. Drag the shortcut you created into that folder.

That's it — it'll now start automatically at every login. To turn it off again, just delete the shortcut from that Startup folder.

## Moving to another computer

Copy the whole `PingTray` folder anywhere (USB drive, cloud folder, etc.) and run `dist\PingTray.exe` on the new machine — nothing else to install. Repeat the steps above on that machine if you want it to autostart there too.

## Watching a different address

By default it pings `1.1.1.1` (a well-known, always-on address used to test general internet connectivity). Easiest way to change it: click the tray icon and enter a new host in the **Settings** box above.

## Monitoring more than one server

You can run several Ping Tray icons side by side — one per server — each remembering its own target and interval independently:

1. Right-click `dist\PingTray.exe` → **Create shortcut**.
2. Right-click the new shortcut → **Properties**.
3. In the **Target** field, add a space and then, for example: `--target=192.168.1.1`
4. Save, and use that shortcut to launch this second icon.

Repeat for as many servers as you want to watch, each with its own shortcut and `--target=`. You can also add `--interval=10` the same way to make that particular icon check every 10 seconds instead of 3. Each shortcut's settings (including anything you later change via the Settings box) are saved separately, so they won't overwrite each other.

## How dist/PingTray.exe was built

The prebuilt exe is compiled straight from `src/PingTray.cs` by `build.ps1`, using the C# compiler that ships inside .NET Framework on every Windows 10/11 machine (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`). No SDK, no NuGet packages, no third-party code — the exact command is:

```
csc.exe /nologo /target:winexe /platform:x64 /out:dist\PingTray.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll src\PingTray.cs
```

SHA-256 of the committed `dist/PingTray.exe`:

```
E7211EA9269C7820A536B9FF045FC29C99629880A2CB8AF39E99436B5063FE0B
```

The exe is **not code-signed**, so SmartScreen may warn about it after downloading (right-click → Properties → **Unblock**, or just build it yourself — see below).

## Don't want to run a stranger's exe?

Fair enough — you don't have to. Everything needed to build it yourself is plain text you can read first (`src\PingTray.cs` and `build.ps1`), and the compiler is already on your machine: run `.\build.ps1` and it recompiles `dist\PingTray.exe` from that source, using only the C# compiler bundled with .NET Framework.

(Note: rebuilding won't reproduce the byte-identical hash above — the C# compiler embeds a timestamp — but what runs is then 100% built from the source you can read.)
