# PingTray privacy policy

Last updated 31 October 2026.

PingTray does not collect or send any personal information. There are no accounts, ads or analytics.

**What it does on the network.** PingTray sends traffic only to the host you choose (1.1.1.1 unless you change it): a ping, a TCP connect or a DNS query, depending on the method you pick. While that host is not answering, PingTray also tries the other two methods against the same host, so it can tell you when one of them works. If you enter a host name, your computer looks it up through its normal DNS setup. Nothing is sent to the author or to anyone else.

**What it keeps on your computer.** Your Settings (host, method, port, interval and colour limits) are saved in a small `settings.ini` file, next to the exe in the zip version or in the app's private storage in the Microsoft Store version. The Store version's copy is removed when you uninstall. Apart from the Start with Windows entry below, nothing else is saved, and no history of readings is kept.

**Start with Windows.** In the zip version, ticking this in Settings adds one entry under `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` (your own account only, no administrator rights), and unticking it removes the entry. The Microsoft Store version does not write to the registry: start at login is switched on in Windows Settings.

**Contact.** Questions about this policy can go to the project's issue tracker: https://github.com/andrewreardon-au/PingTray/issues

If this policy changes, the new version will be published here with a new date.
