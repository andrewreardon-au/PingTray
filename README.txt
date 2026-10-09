PING TRAY

v1.4 -- 2026-10-09
Author: Andrew Reardon, andrewreardon@gmail.com

Shows your network latency as a number in the system tray, right next to
the clock.


WHAT THE NUMBER MEANS

  Green         Good -- under 50ms
  Orange        OK -- 50 to 150ms
  Red           Slow -- 150ms or higher
  Black "0"     Offline -- no response

Hover over the icon to see the exact number and these ranges. It checks
every 5 seconds by default.

The number is drawn big and bold with a thin outline, so it reads on any
taskbar colour. On a light Windows taskbar the colours switch to darker
shades, and the offline "0" has a light outline so it shows up on a dark
taskbar too.


WHAT'S NEW IN v1.4

  * Bigger, bolder numbers: roughly 1.5 to 2 times taller in the tray than
    before, with an outline so they stay readable on any background.
  * Follows your Windows light/dark taskbar: the old light green and orange
    were hard to read on a light taskbar.
  * The offline "0" shows on a dark taskbar (it used to be black on dark).
  * Nothing else changed: pinging, settings and the command-line options
    work as before.


GETTING STARTED

  1. Double-click PingTray.exe (in the dist folder).
  2. Don't see it? Click the small ^ arrow near the clock to show hidden
     tray icons.
  3. To close it, right-click the icon and choose Exit.


KEEPING IT ALWAYS VISIBLE

By default, Windows may tuck new tray icons away under the ^ hidden-icons
arrow instead of showing them next to the clock. To pin it visibly:

  1. Right-click the taskbar, then choose Taskbar settings.
  2. Click Other system tray icons.
  3. Find PingTray in the list and toggle it On.


STARTING AUTOMATICALLY ON LOGIN

To have Ping Tray launch on its own every time you sign in:

  1. Right-click PingTray.exe (in the dist folder) and choose Create
     shortcut.
  2. Press Win + R, type shell:startup, and hit Enter -- this opens your
     Startup folder.
  3. Drag the shortcut you created into that folder.

That's it -- it'll now start automatically at every login. To turn it off
again, just delete the shortcut from that Startup folder.


MOVING TO ANOTHER COMPUTER

Copy the whole PingTray folder anywhere (USB drive, cloud folder, etc.)
and run dist\PingTray.exe on the new machine -- nothing else to install.
Repeat the steps above on that machine if you want it to autostart there
too.


WATCHING A DIFFERENT ADDRESS

By default it pings 1.1.1.1 (a well-known, always-on address used to test
general internet connectivity). If you'd rather watch something else,
like your router:

  1. Right-click PingTray.exe (in the dist folder) and choose Create
     shortcut.
  2. Right-click the new shortcut and choose Properties.
  3. In the Target field, add a space and then, for example:
     --target=192.168.1.1
  4. Save, and use that shortcut instead.

You can also add --interval=10 the same way to check every 10 seconds
instead of 5.


COMMAND-LINE OPTIONS

Running from a terminal? All options have short forms too:

  -t <host>, --target=<host>   host to ping (default 1.1.1.1)
  -i <sec>,  --interval=<sec>  seconds between pings (default 5)
  -h, --help                   show help and exit
  -v, --version                show version info and exit

Started with no options, Ping Tray resumes whatever was last saved from
the Settings box (settings.ini next to the exe). With any option given,
the command line always wins -- the saved file is ignored and flags are
never saved.

Right-click the tray icon and choose About PingTray... for version and
contact details.
