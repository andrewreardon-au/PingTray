# PingTray

**v2.0** (2026-10-31) by Andrew Reardon

Shows your network latency as a number in the system tray.

![PingTray's green 22 ms icon in the system tray, with the hover tooltip "Expected on Wi-Fi: under 50 ms" and "Running as expected"](screenshots/PingTray-screenshot_tray.png)

## What the number means

| Colour | Meaning (the default limits; you can [change them](#tuning-the-colours)) |
|---|---|
| Green | Good: under 50 ms |
| Orange | OK: 50 ms up to (not including) 150 ms |
| Red | Slow: 150 ms or higher |
| Grey × | Offline: no response |

It checks every 5 seconds by default. Hover over the icon to see the exact number, which connection-type [preset](#tuning-the-colours) your limits match and what it counts as normal, and whether the reply is as expected for that preset. When it is not, the [ready reckoner](#the-ready-reckoner) adds what it feels like.

The number is drawn big with a thin outline, so it reads on any taskbar colour. On a light Windows taskbar the colours switch to darker shades. A reply under 1 ms shows as `<1`, and 1000 ms or more as `1s+`.

A single missed reply doesn't flip it to offline, which is normal on flaky connections like 4G. Until the **3rd miss in a row** the icon keeps your last good reading but in grey, and hovering shows something like `no response 1/3 (last: 23ms)`. Grey digits mean the number is stale, a grey × means offline, and the next reply brings the colour straight back.

## Getting started

**Microsoft Store:** PingTray is on the [Microsoft Store](https://apps.microsoft.com/detail/9MV371JKHPVD). It installs and updates through the Store, and its settings are private to the app and removed if you uninstall it. Open it from the Start menu.

**Zip:** there is no installer. [Download the latest release zip](https://github.com/andrewreardon-au/PingTray/releases/latest), extract it and double-click `dist\PingTray.exe`. To move it to another computer, copy the whole folder.

Then:

1. Don't see the icon? Click the small **^** arrow near the clock to show hidden tray icons.
2. To close it, right-click the icon > **Exit**.

![The right-click menu](screenshots/PingTray-screenshot_menu.png)

### Keeping the icon visible

Windows may tuck new tray icons away under the **^** arrow. To pin it next to the clock:

1. Right-click the taskbar > **Taskbar settings**.
2. Click **Other system tray icons**.
3. Find **PingTray** in the list and switch it **On**. (On Windows 10 the page is called **Select which icons appear on the taskbar**.)

### Starting automatically at login

In the Settings box, tick **Start with Windows** to start PingTray when you sign in. Untick it to stop. (In the Store version that line is a link to Windows Settings > Apps > Startup instead.)

In the zip version, a shortcut works too, if you want one that uses `--target=` options: right-click `dist\PingTray.exe` > **Create shortcut**, press `Win + R`, type `shell:startup`, and drag the shortcut into the folder that opens. Delete it from there to turn it off.

## Changing what it watches

Click the tray icon (or right-click > **Settings...**) to open the Settings box. You can change the target host, how it checks (ping, TCP connect or a DNS query), how often it checks, and what counts as green, orange and red. Click **OK** and it applies at once, with no restart; until the first reply from a new target the icon shows the PingTray logo, not the old target's number. Your choice is remembered for next time (when you start it normally, without command-line options).

![The Settings box](screenshots/PingTray-screenshot_settings.png)

Settings checks what you type before it closes. If something is wrong, the box stays open, the hint at the bottom turns red and says what to fix, and the cursor goes to that box. The target can be a host name (`example.com`, `router`), an IP address (four numbers from 0 to 255 separated by dots, like `1.1.1.1`) or an IPv6 address. Something like `1.1.1` or `010.1.1.1` is refused rather than guessed at: Windows would read them as 1.1.0.1 and 8.1.1.1, and you would be watching a different address from the one you typed. The interval is 1 to 86400 seconds, a port 1 to 65535, and the colour limits 1 to 2000 ms with red above orange.

By default it pings `1.1.1.1` (Cloudflare's public DNS). It is always on and answers from a nearby server in most parts of the world, so the reading reflects your own connection. `8.8.8.8` (Google) is a good alternative.

## Tuning the colours

What counts as "good" depends on the connection: 30 ms is slow on a LAN and brilliant on satellite. Open **Settings** and pick your **Connection type** to fill in suggested limits, or type your own **Orange from** and **Red from** (in milliseconds). Anything under the orange limit is green.

| Connection type | Orange from | Red from | Faster than expected below |
|---|---|---|---|
| LAN (router or server) | 5 ms | 20 ms | - |
| Wired broadband / fibre | 20 ms | 60 ms | - |
| Wi-Fi / broadband (default) | 50 ms | 150 ms | - |
| 5G | 60 ms | 120 ms | 10 ms |
| 4G | 80 ms | 200 ms | 25 ms |
| Satellite (geostationary) | 800 ms | 1200 ms | 450 ms |
| 90s dial-up | 250 ms | 500 ms | 90 ms |

The satellite limits are for a geostationary link (a round trip of roughly 500 ms or more). A low-orbit service such as Starlink behaves like ordinary broadband, so pick Wi-Fi / broadband for it. These are suggestions, not measurements of your line. Type your own numbers and the type just reads "Custom". The tooltip always shows the preset and what it counts as normal, e.g. `Expected on Wi-Fi: under 50 ms`, or `Expected on 4G: 25 to 80 ms` for a preset with a floor (it says `Expected (custom)` when your numbers match none of them). From the command line: `--orange=<ms>` and `--red=<ms>`.

Under that line the tooltip says in words how the reply compares with that preset: **Running as expected** while it is green, **Slower than expected** when it is orange, **Much slower than expected** when it is red. A green reply can also be **Faster than expected**: quicker than that kind of connection ever manages (the last column above), so it is probably not that kind of connection at all. A 22 ms reply on the 4G preset, say, is not what 4G gives. LAN, wired and Wi-Fi have no such floor, because a fast reply is always normal there, and neither do limits you type yourself ("Custom").

```
Ping to example.org: 26 ms
Expected on Wi-Fi: under 50 ms
Running as expected
```

## The ready reckoner

When a reply is not what your preset expects, slower or faster, the tooltip adds a tongue-in-cheek line saying which kind of connection a ping like that feels like, and the year that technology debuted (`-present` if it is still in everyday use):

```
Ping to example.org: 172 ms
Expected on Wi-Fi: under 50 ms
Much slower than expected
Like 56K dial-up from 1997
```

```
Ping to example.org: 22 ms
Expected on 4G: 25 to 80 ms
Faster than expected
Like Wi-Fi 6 from 2019-present
```

| Ping | Feels like |
|---|---|
| under 4 ms | gigabit Ethernet from 1999-present |
| 4 to 24 ms | Wi-Fi 6 from 2019-present |
| 25 to 46 ms | 5G from 2019-present |
| 47 to 79 ms | 4G from 2009-present |
| 80 to 136 ms | 3G from 2001 |
| 137 to 209 ms | 56K dial-up from 1997 |
| 210 to 329 ms | 28.8K dial-up from 1994 |
| 330 to 579 ms | 14.4K dial-up from 1991 |
| 580 to 1094 ms | a 2400-baud modem from 1984 |
| 1095 ms and over | ARPANET from 1969 |

For example, a wired connection that slips to 22 ms reads "Like Wi-Fi 6 from 2019-present", and Wi-Fi that slips to 60 ms reads "Like 4G from 2009-present". A reply that is as expected just says so. It is order-of-magnitude fun, not a measurement: real lines vary a lot.

Windows shows only a limited amount of tooltip text. If the full wording would not fit (a very long host name, say, together with the "usually" note below), the reckoner line is dropped first, and the note last.

## One odd reading doesn't change the wording

Now and then a single ping is far slower (or faster) than the ones around it. So the wording under the number ("Running as expected", "Slower than expected" and the reckoner line) is based on the median of the last five readings (the middle one when you put them in order), not on the latest alone. One odd reading changes nothing; it takes three of the last five to move the wording, about 15 seconds at the default 5-second interval.

The icon is different: it always shows the latest reading, in that reading's own colour. When the wording would have been different for the latest reading alone, the first line of the tooltip says what the wording is based on. Here one 300 ms reading (a red icon) sits in a run of readings around 30 ms:

```
Ping to example.org: 300 ms (usually 30 ms)
Expected on Wi-Fi: under 50 ms
Running as expected
```

Readings older than two minutes are not used, and the count starts again after an outage (three misses in a row) or when you change the target, method or port. So after the laptop has been asleep, the first reading stands on its own.

## When ping is blocked

Some networks drop ping (ICMP) while everything else works. Open **Settings** and change **Method**:

| Method | What it times | Port |
|---|---|---|
| **Ping (ICMP)** (the default) | an ICMP echo | - |
| **TCP connect** | how long a TCP connection takes to open, the same handshake as opening a website | 443 |
| **DNS query (UDP)** | how long a DNS server takes to answer one small query (for the list of root name servers, which every DNS server can answer). Point it at a DNS server such as `1.1.1.1`, `8.8.8.8` or your own | 53 |

TCP and DNS are one round trip too, so they read close to ping (a few milliseconds higher), and none of the three needs admin rights. From the command line: `--method=tcp` or `--method=udp`, and `--port=<n>`. Each method remembers its own port.

You don't have to guess which one is blocked. If the method you chose goes offline (three misses in a row) but another one still gets through, the tooltip says so:

```
Ping to example.org: offline (TimedOut) [3x]
Ping looks blocked, but TCP works.
Switch it in Settings > Method.
```

The same goes for any pair, e.g. "TCP looks blocked, but DNS works" or "DNS looks blocked, but ping works". If nothing gets through, it just says offline. To find out, PingTray tries the other two methods against the same host while the chosen one is not answering.

TCP connect only does the handshake and sends nothing else, so pick a port the target really listens on (443 for websites, 22 for SSH, 445 for file shares, 3389 for Remote Desktop), because a closed port counts as no reply. The DNS query sends one tiny request and needs a server that answers DNS. Any answer counts, even an error.

Behind a mandatory web proxy neither ping nor a direct TCP connection reaches the internet, so:

- Watch your internal servers (by ping or TCP connect): type their names or addresses as the target.
- Time the path to the proxy itself: set Method to *TCP connect* and use the proxy's host name as the target and its port as the port (your IT team can tell you, or look under Windows **Settings > Network & internet > Proxy**).

## Monitoring more than one server

You can run several PingTray icons side by side, one per server, each with its own target and interval. In the zip version:

1. Right-click `dist\PingTray.exe` > **Create shortcut**.
2. Right-click the new shortcut > **Properties**.
3. In the **Target** field, add a space and then, for example: `--target=192.168.1.1`
4. Save, and use that shortcut to launch this second icon.

In the Store version, run `PingTray.exe --target=192.168.1.1` from `Win + R` or a terminal.

Repeat for as many servers as you want to watch, each with its own `--target=`. You can also add `--interval=10` the same way to make that icon check every 10 seconds instead of 5, or `--method=tcp --port=22` to watch a server by TCP connect. The same host checked by ping, TCP and DNS counts as three icons. A shortcut with options always does exactly what its options say: choices saved in the Settings box only apply when PingTray is started without options.

## Command-line options

For terminal users:

```
PingTray.exe [options]

  -t <host>, --target=<host>   host to check (default 1.1.1.1)
  -i <sec>,  --interval=<sec>  seconds between checks (default 5)
  --method=<icmp|tcp|udp>      ping (default), a TCP connection, or a DNS query
  --port=<n>                   port for tcp (default 443) or udp (default 53)
  --orange=<ms>                a reply this slow or slower is orange (default 50)
  --red=<ms>                   a reply this slow or slower is red (default 150)
  -h, --help                   show this help and exit
  -v, --version                show version info and exit
```

A `--target` that is not a host name or an IP address (for example `-t 1.1.1`) is reported and PingTray does not start, so a typo in a shortcut is noticed instead of quietly watching something else. Other bad values (`-i 0`, `--orange=5000`) are ignored, and so is any bad value in `settings.ini`.

`-h` and `-v` print to the console when run from one (or show a small dialog if double-clicked). Started with no options, PingTray resumes whatever was last saved from the Settings box (`settings.ini` next to the exe in the zip, or in the app's private storage in the Store version). With any valid option given, the command line always wins: the saved file is ignored and never overwritten by flags. Changes you make in Settings during such a run apply until you close it, and are not saved. The Start with Windows tick is the exception: it is a Windows setting, so it takes effect when you press OK, and it starts PingTray without options. Launching a target that is already being watched shows a notice instead of starting a duplicate. For version info, right-click the tray icon > **About PingTray...**

![The About box](screenshots/PingTray-screenshot_about.png)

## What's new in v2.0

- **A tidier Settings box:** the settings are grouped under **Connection** and **Colour limits**, the numbers line up on the right, and the box uses the standard Windows font.
- **Options are never saved by accident:** if PingTray was started with options such as `--target=`, pressing OK in Settings no longer overwrites the settings you saved earlier.
- **Enter closes the About box** (it used to open the project page).
- Your saved settings carry over unchanged.

## What's new in v1.9

- **Numbers that look like one font:** every two-digit reading is now drawn at the same height and weight (11 used to look bolder than 28), in Segoe UI Semibold. A reply under 1 ms reads `<1` (it was 0) and 1 second or more reads `1s+` (it was 999+).
- **A missed reply greys the number:** until the third miss the icon keeps your last reading in grey instead of its colour. The offline icon is now a grey × (it was a "0"), so the two can't be mixed up.
- **Settings and About take focus:** opening either from the tray brings it to the front, so you can type straight away.
- **Changing the target starts clean:** the icon goes back to the logo until the first reply from the new target. Before, the old target's number could linger, or its late reply could turn up under the new target.

## What's new in v1.8

- **The tooltip says what is normal, in words:** the second line now reads `Expected on Wi-Fi: under 50 ms` (or `Expected on 4G: 25 to 80 ms` for a preset with a floor). It is the same range that "Running as expected" uses, so the two agree.

Older changes are in the release notes for each version on the [releases page](https://github.com/andrewreardon-au/PingTray/releases).

## What's in this repo

| Path | What it is |
|---|---|
| `src/PingTray.cs` | The entire app: one plain C# file, no dependencies beyond .NET Framework |
| `src/PingTray.ico` | The exe's embedded icon |
| `dist/PingTray.exe` | Prebuilt, ready-to-run binary (see [How dist/PingTray.exe was built](#how-distpingtrayexe-was-built)) |
| `build.ps1` | Rebuilds `dist/PingTray.exe` from source on your own machine |
| `README.txt` | This guide as plain text, for the zip |
| `screenshots/` | The images in this README |
| `PRIVACY.md` | The privacy policy |
| `LICENSE` | MIT |
| `store/`, `docs/`, `build-store.ps1` | Microsoft Store packaging and notes (not in the release zip) |

## How dist/PingTray.exe was built

The prebuilt exe is compiled straight from `src/PingTray.cs` by `build.ps1`, using the C# compiler that ships inside .NET Framework on every Windows 10/11 machine (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`). No SDK, no NuGet packages, no third-party code. The exact command is:

```
csc.exe /nologo /target:winexe /platform:x64 /out:dist\PingTray.exe /win32icon:src\PingTray.ico /reference:System.Windows.Forms.dll /reference:System.Drawing.dll src\PingTray.cs
```

SHA-256 of the committed `dist/PingTray.exe`:

```
C2EFF31724055F1A8D56DDC6FE7B0A094050FCC750834B6C278657CA7D5EAF18
```

The exe in the zip is **not code-signed**, so SmartScreen may warn about it after downloading. Right-click the zip > Properties > **Unblock** before extracting, or build it yourself (below). The Store version is signed by the Store.

## Building it yourself

Everything needed to build it yourself is plain text you can read first (`src\PingTray.cs` and `build.ps1`), and the compiler is already on your machine. Run `.\build.ps1` and it recompiles `dist\PingTray.exe` from that source, using only the C# compiler bundled with .NET Framework. It all happens at user level with no admin rights, which suits tightly controlled servers and laptops.

Rebuilding won't reproduce the byte-identical hash above, because the C# compiler embeds a timestamp.

## License

MIT, see [LICENSE](LICENSE).
