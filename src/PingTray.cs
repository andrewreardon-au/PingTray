using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

// Embedded VERSIONINFO (Explorer's Properties → Details). Kept in this file
// so the single-source-file build stays true; versions derive from the same
// Version const that -v and the About dialog show.
[assembly: AssemblyTitle("PingTray")]
[assembly: AssemblyProduct("PingTray")]
[assembly: AssemblyDescription("Live ping latency in the system tray")]
[assembly: AssemblyCopyright("© 2026 Andrew Reardon")]
[assembly: AssemblyVersion(PingTray.Version + ".0.0")]
[assembly: AssemblyFileVersion(PingTray.Version + ".0.0")]

internal static class PingTray
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WriteConsoleInput(IntPtr hConsoleInput, INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsWritten);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr hFile);

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct KEY_EVENT_RECORD
    {
        public bool bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_INPUT_HANDLE = -10;
    private const ushort KEY_EVENT = 1;
    private const ushort VK_RETURN = 0x0D;
    private const uint FILE_TYPE_CHAR = 2;

    internal const string Version = "1.6";
    private const string VersionDate = "2026-10-20";
    private const string Author = "Andrew Reardon";
    private const string AuthorEmail = "andrewreardon@gmail.com";

    private static string Target = "1.1.1.1";
    private static int IntervalMs = 5000;

    // The longest interval accepted, in seconds (a day). It also keeps seconds * 1000 inside an int: a
    // bigger number wrapped to a negative timer interval, which threw.
    private const int MaxIntervalSeconds = 86400;

    // How a reading is taken: an ICMP echo ("ping", the default), the time a TCP connection takes
    // to open, or the time a DNS server takes to answer one small query over UDP. Each is one round
    // trip, so they read about the same, and the other two work where ICMP is blocked. None needs
    // admin rights. Tunable in the Settings dialog or with --method / --port.
    private enum Method { Icmp, Tcp, Udp }
    private static readonly string[] MethodNames = { "Ping (ICMP)", "TCP connect", "DNS query (UDP)" };
    private const int DefaultTcpPort = 443;
    private const int DefaultUdpPort = 53;
    private static Method ProbeMethod = Method.Icmp;
    private static int TcpPort = DefaultTcpPort;
    private static int UdpPort = DefaultUdpPort;

    // The port of the TCP or UDP method (ping has none).
    private static int PortFor(Method method)
    {
        return method == Method.Udp ? UdpPort : TcpPort;
    }

    // Colour limits in ms: a reply under OrangeMs is green, under RedMs orange, otherwise red.
    // Tunable in the Settings dialog (with suggestions per connection type) or with
    // --orange / --red. MaxThresholdMs is the ping timeout: nothing slower is ever shown.
    private const int DefaultOrangeMs = 50;
    private const int DefaultRedMs = 150;
    private const int MaxThresholdMs = 2000;
    private static int OrangeMs = DefaultOrangeMs;
    private static int RedMs = DefaultRedMs;

    // Set by ParseArgs when the corresponding option was given explicitly.
    // Command-line options always win: if either was passed, settings.ini
    // is not loaded at all (defaults + flags only), and flags are never
    // persisted — only Settings-dialog edits write settings.ini.
    private static bool _targetFromCli;
    private static bool _intervalFromCli;
    private static bool _coloursFromCli;
    private static bool _methodFromCli;
    private static int _cliPort;   // --port=<n>: applied to the TCP or UDP port once the method is known, whatever the order of the flags

    // A single dropped ICMP reply is common on flaky links (e.g. 4G) and
    // isn't necessarily "down" — only flip to offline after this many
    // consecutive failed attempts. Each attempt waits up to PingTimeoutMs
    // before being counted as failed.
    private const int FailureThreshold = 3;
    private const int PingTimeoutMs = 2000;
    private static int _consecutiveFailures = 0;
    private static long _lastGoodMs = -1;

    // The last few good readings of the host and method being watched, oldest first, each with the time
    // it was taken. The tooltip's wording is judged on their median, not on the latest reading alone, so
    // one odd reading (a spike, a lucky fast one) cannot flip it; the icon still shows the latest.
    // Cleared when the host, method or port changes and when the app goes offline; readings older than
    // MedianMaxAge (a sleeping laptop, a long interval) are not used.
    private const int MedianWindow = 5;
    private static readonly TimeSpan MedianMaxAge = TimeSpan.FromMinutes(2);
    private struct Sample { public long Ms; public DateTime At; }
    private static readonly List<Sample> _recent = new List<Sample>();

    // Set while offline when ANOTHER method still gets through (as the Method number, else -1):
    // that is what a firewall blocking one of them looks like, and the tooltip says so.
    private static int _otherWorking = -1;

    private static string ConfigPath;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, System.Text.StringBuilder packageFullName);

    // True when running from an installed MSIX package (the Microsoft Store build). The install folder is
    // then read-only and per-user files belong in LocalAppData, and "start at login" is a package setting
    // that Windows owns rather than a Run-key entry.
    private static bool IsPackaged()
    {
        try
        {
            int length = 0;
            const int AppModelErrorNoPackage = 15700;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    // Packaged: %LOCALAPPDATA%\PingTray\settings.ini (the package folder cannot be written to). Windows
    // redirects that into the package's private LocalCache, which an uninstall removes.
    // Otherwise next to the exe, so the unzipped folder stays portable.
    private static string SettingsFilePath()
    {
        if (IsPackaged())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PingTray", "settings.ini");
        }
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
    }

    // "Start with Windows" for the unzipped build: a per-user Run-key entry that launches this exe with no
    // options, so it resumes the settings saved from the Settings box. (The Store build cannot use the Run
    // key; its startup entry is declared in the package manifest and switched in Windows Settings.)
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PingTray";

    private static string RunCommand()
    {
        return "\"" + Application.ExecutablePath + "\"";
    }

    private static bool GetRunAtLogin()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            {
                // Any entry counts, even one left pointing at an old folder after the exe was moved:
                // OK then rewrites it to this exe (ticked) or removes it (unticked).
                return k != null && k.GetValue(RunValueName) != null;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void SetRunAtLogin(bool on)
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (on) k.SetValue(RunValueName, RunCommand());
                else k.DeleteValue(RunValueName, false);
            }
        }
        catch
        {
            // Best-effort, like saving the settings: a locked-down profile just keeps it off.
        }
    }

    private static NotifyIcon _notifyIcon;
    private static Ping _ping;
    private static Icon _currentIcon;
    private static Timer _timer;

    [STAThread]
    private static void Main(string[] args)
    {
        if (!ParseArgs(args))
        {
            return;
        }

        ConfigPath = SettingsFilePath();

        // No explicit options: resume whatever was being monitored last
        // time (saved by the Settings dialog). Any explicit option means
        // the command line fully describes this run — built-in defaults
        // plus the given flags; the saved file is ignored.
        if (!_targetFromCli && !_intervalFromCli && !_coloursFromCli && !_methodFromCli)
        {
            LoadSettings();
        }

        // Prevent duplicate instances monitoring the same target the same way (different
        // targets, or the same one by ping, TCP and DNS, can run side by side).
        bool createdNew;
        string watching = Target + (ProbeMethod == Method.Tcp ? " by TCP on port " + TcpPort
            : (ProbeMethod == Method.Udp ? " by DNS query on port " + UdpPort : ""));
        var mutex = new System.Threading.Mutex(true,
            "Global\\PingTray_SingleInstance_" + Target + (ProbeMethod == Method.Tcp ? "_tcp" + TcpPort
                : (ProbeMethod == Method.Udp ? "_udp" + UdpPort : "")), out createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "PingTray is already running for " + watching + " — look for its icon in the tray (or under the ^ overflow arrow near the clock).\n\n" +
                "To watch a second host at the same time, launch with a different target, e.g.:\n" +
                "PingTray.exe -t 1.0.0.1",
                // MessageBoxIcon.None: an icon would play the system
                // notification sound (same beep the About box had).
                "PingTray", MessageBoxButtons.OK, MessageBoxIcon.None);
            return;
        }

        Application.EnableVisualStyles();
        _ping = new Ping();

        var menu = new ContextMenuStrip();
        var settingsItem = menu.Items.Add("Settings...");
        settingsItem.Click += (s, e) => ShowSettingsDialog();
        var aboutItem = menu.Items.Add("About PingTray...");
        aboutItem.Click += (s, e) => ShowAboutDialog();
        menu.Items.Add(new ToolStripSeparator());
        var exitItem = menu.Items.Add("Exit");
        exitItem.Click += (s, e) => Application.Exit();

        _notifyIcon = new NotifyIcon();
        // Placeholder until the first (async) ping paints a number —
        // without this the tray slot is invisible for the first attempt
        // (up to 2s if the target is slow/down).
        _notifyIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        _notifyIcon.Text = "Ping: ...";
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.Visible = true;
        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowSettingsDialog();
            }
        };

        _timer = new Timer();
        _timer.Interval = IntervalMs;
        _timer.Tick += (s, e) => UpdatePing();
        UpdatePing();
        _timer.Start();

        Application.ApplicationExit += (s, e) =>
        {
            _timer.Stop();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            if (_currentIcon != null)
            {
                var h = _currentIcon.Handle;
                _currentIcon.Dispose();
                DestroyIcon(h);
            }
            GC.KeepAlive(mutex);
        };

        Application.Run();
    }

    // Returns false if the process should exit without starting the tray
    // icon (-h/-v handled and printed).
    private static bool ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg == "-h" || arg == "-?" || arg == "/?" ||
                string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintOrShow(UsageText());
                return false;
            }
            else if (arg == "-v" || string.Equals(arg, "--version", StringComparison.OrdinalIgnoreCase))
            {
                PrintOrShow(VersionText());
                return false;
            }
            else if (arg.StartsWith("--target=", StringComparison.OrdinalIgnoreCase) ||
                     arg.StartsWith("-t=", StringComparison.Ordinal))
            {
                string value = arg.Substring(arg.IndexOf('=') + 1).Trim();
                if (value.Length > 0 && !AcceptTarget(value))
                {
                    return false;
                }
            }
            else if (arg == "-t" && i + 1 < args.Length)
            {
                string value = args[++i].Trim();
                if (value.Length > 0 && !AcceptTarget(value))
                {
                    return false;
                }
            }
            else if (arg.StartsWith("--interval=", StringComparison.OrdinalIgnoreCase) ||
                     arg.StartsWith("-i=", StringComparison.Ordinal))
            {
                int seconds;
                if (TryInterval(arg.Substring(arg.IndexOf('=') + 1), out seconds))
                {
                    IntervalMs = seconds * 1000;
                    _intervalFromCli = true;
                }
            }
            else if (arg == "-i" && i + 1 < args.Length)
            {
                int seconds;
                if (TryInterval(args[++i], out seconds))
                {
                    IntervalMs = seconds * 1000;
                    _intervalFromCli = true;
                }
            }
            else if (arg.StartsWith("--orange=", StringComparison.OrdinalIgnoreCase) ||
                     (string.Equals(arg, "--orange", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length))
            {
                int ms;
                if (TryMs(arg.IndexOf('=') >= 0 ? arg.Substring(arg.IndexOf('=') + 1) : args[++i], out ms))
                {
                    OrangeMs = ms;
                    _coloursFromCli = true;
                }
            }
            else if (arg.StartsWith("--red=", StringComparison.OrdinalIgnoreCase) ||
                     (string.Equals(arg, "--red", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length))
            {
                int ms;
                if (TryMs(arg.IndexOf('=') >= 0 ? arg.Substring(arg.IndexOf('=') + 1) : args[++i], out ms))
                {
                    RedMs = ms;
                    _coloursFromCli = true;
                }
            }
            else if (arg.StartsWith("--method=", StringComparison.OrdinalIgnoreCase) ||
                     (string.Equals(arg, "--method", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length))
            {
                Method m;
                if (TryMethod(arg.IndexOf('=') >= 0 ? arg.Substring(arg.IndexOf('=') + 1) : args[++i], out m))
                {
                    ProbeMethod = m;
                    _methodFromCli = true;
                }
            }
            else if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase) ||
                     (string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length))
            {
                int port;
                if (TryPort(arg.IndexOf('=') >= 0 ? arg.Substring(arg.IndexOf('=') + 1) : args[++i], out port))
                {
                    _cliPort = port;
                    _methodFromCli = true;
                }
            }
        }

        // --port belongs to the TCP or UDP method, whichever was chosen, in either order on the line.
        if (_cliPort > 0)
        {
            if (ProbeMethod == Method.Udp) UdpPort = _cliPort; else TcpPort = _cliPort;
        }

        // Orange has to come before red. If the two flags disagree, ignore both, like any
        // other bad option (and let settings.ini apply as usual).
        if (_coloursFromCli && RedMs <= OrangeMs)
        {
            OrangeMs = DefaultOrangeMs;
            RedMs = DefaultRedMs;
            _coloursFromCli = false;
        }
        return true;
    }

    // A colour limit in whole milliseconds: 1 up to the ping timeout.
    private static bool TryMs(string s, out int ms)
    {
        return int.TryParse(s.Trim(), out ms) && ms >= 1 && ms <= MaxThresholdMs;
    }

    // "icmp" (or "ping"), "tcp", or "udp" (or "dns"), in any case.
    private static bool TryMethod(string s, out Method method)
    {
        string v = s.Trim().ToLowerInvariant();
        method = v == "tcp" ? Method.Tcp : (v == "udp" || v == "dns" ? Method.Udp : Method.Icmp);
        return v == "icmp" || v == "ping" || v == "tcp" || v == "udp" || v == "dns";
    }

    // The name a method is saved under in settings.ini.
    private static string MethodKey(Method method)
    {
        return method == Method.Tcp ? "tcp" : (method == Method.Udp ? "udp" : "icmp");
    }

    // A TCP or UDP port number: 1 to 65535.
    private static bool TryPort(string s, out int port)
    {
        return int.TryParse(s.Trim(), out port) && port >= 1 && port <= 65535;
    }

    // A port box from the Settings dialog: a valid number replaces the port, anything else leaves the old
    // port as it was. (SettingsProblem has already refused a bad port for the method that is chosen; the
    // other method's slot may hold anything.)
    private static void AdoptPort(string text, ref int port)
    {
        int p;
        if (TryPort(text, out p)) port = p;
    }

    // A polling interval in whole seconds: 1 up to MaxIntervalSeconds.
    private static bool TryInterval(string s, out int seconds)
    {
        return int.TryParse(s.Trim(), out seconds) && seconds >= 1 && seconds <= MaxIntervalSeconds;
    }

    // A target given on the command line. A bad one is reported and ends the run: unlike a bad colour limit
    // it has no harmless default, and carrying on with some other host without a word would show that
    // host's latency under the name the user typed.
    private static bool AcceptTarget(string value)
    {
        string problem = TargetProblem(value);
        if (problem != null)
        {
            Environment.ExitCode = 1;
            PrintOrShow("PingTray: cannot watch \"" + value + "\". " + problem + "\r\nRun PingTray.exe -h for the options.");
            return false;
        }
        Target = value;
        _targetFromCli = true;
        return true;
    }

    // The longest host accepted. The single-instance mutex is named "Global\PingTray_SingleInstance_" + host +
    // at most "_tcp65535", and a Windows mutex name may have 260 characters: 31 + 220 + 9. (A host name may
    // run to 253 characters, but one over this length crashed the app at start-up.)
    private const int MaxHostLength = 220;
    private const string PortInHost = "Enter just the host name or IP address. The port is a separate setting (the Port box, or --port=<n>).";

    // What is wrong with a host as typed - a name, an IPv4 address or an IPv6 address - in plain words, or
    // null if it is fine. Text of only digits and dots is taken for an IPv4 address and has to be a proper
    // dotted quad: left to .NET, "1.1.1" means 1.1.0.1 and "010.1.1.1" means 8.1.1.1 (octal), so the app
    // would quietly watch some other address than the one typed. A web address or path is turned away
    // too (a backslash in the name would also break the single-instance mutex's name).
    private static string TargetProblem(string host)
    {
        if (host.Length == 0) return "Enter a host name or IP address, such as 1.1.1.1 or example.com.";
        if (host.Length > MaxHostLength) return "That host name is too long (" + MaxHostLength + " characters at most).";
        if (host.IndexOfAny(new[] { '/', '\\', '?', '#' }) >= 0) return "Enter just the host name or IP address, without http:// or a path.";
        int colons = 0;
        bool digitsAndDots = true;
        foreach (char c in host)
        {
            if (c == ':') colons++;
            if (c != '.' && (c < '0' || c > '9')) digitsAndDots = false;
        }
        if (colons >= 2)
        {
            // "[::1]:443": .NET reads a bracketed address followed by a port, and drops the port.
            int close = host.IndexOf(']');
            if (close >= 0 && close < host.Length - 1) return PortInHost;
            IPAddress v6;
            return (IPAddress.TryParse(host, out v6) && v6.AddressFamily == AddressFamily.InterNetworkV6) ? null : "That is not a valid IPv6 address.";
        }
        if (colons == 1) return PortInHost;
        if (digitsAndDots)
        {
            return IsDottedQuad(host) ? null : "That is not a valid IP address. Use four numbers from 0 to 255 separated by dots, with no leading zeros, like 1.1.1.1.";
        }
        return IsHostName(host) ? null : "That is not a valid host name. Use letters, digits and hyphens, with parts separated by dots and no spaces, like example.com.";
    }

    // Four numbers 0-255 separated by dots, written without leading zeros (some parsers read 010 as octal).
    private static bool IsDottedQuad(string s)
    {
        string[] parts = s.Split('.');
        if (parts.Length != 4) return false;
        foreach (string part in parts)
        {
            if (part.Length == 0 || part.Length > 3) return false;
            if (part.Length > 1 && part[0] == '0') return false;
            if (int.Parse(part) > 255) return false;   // only digits get here
        }
        return true;
    }

    // Parts of 1-63 letters, digits, hyphens or underscores (not starting or ending with a hyphen) joined
    // by dots; one final dot (a fully qualified name) is fine. Single names such as a router's are fine
    // too, and so are non-English letters (see AsciiHost): this turns away typos, it does not check that
    // the name exists. The length is capped by the caller (MaxHostLength).
    private static bool IsHostName(string s)
    {
        if (s.EndsWith(".", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
        if (s.Length == 0) return false;
        foreach (string part in s.Split('.'))
        {
            if (part.Length == 0 || part.Length > 63) return false;
            if (part[0] == '-' || part[part.Length - 1] == '-') return false;
            foreach (char c in part)
            {
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;
            }
        }
        return true;
    }

    // What is wrong with the entries in the Settings dialog, in plain words, or null if all are fine. field
    // says which box the cursor belongs in: 0 host, 1 port, 2 interval, 3 orange, 4 red. The port is only
    // looked at when the chosen method has one.
    private static string SettingsProblem(string host, bool portNeeded, string port, string interval, string orange, string red, out int field)
    {
        int n, o, r;
        field = 0;
        string problem = TargetProblem(host.Trim());
        if (problem != null) return problem;
        field = 1;
        if (portNeeded && !TryPort(port, out n)) return "Port: enter a whole number from 1 to 65535.";
        field = 2;
        if (!TryInterval(interval, out n)) return "Interval: enter a whole number of seconds from 1 to " + MaxIntervalSeconds + ".";
        field = 3;
        if (!TryMs(orange, out o)) return "Orange from: enter a whole number of milliseconds from 1 to " + MaxThresholdMs + ".";
        field = 4;
        if (!TryMs(red, out r)) return "Red from: enter a whole number of milliseconds from 1 to " + MaxThresholdMs + ".";
        if (r <= o) return "Red from must be higher than Orange from.";
        return null;
    }

    private static string UsageText()
    {
        return "PingTray " + Version + " - live ping latency in the system tray\r\n" +
               "\r\n" +
               "Usage: PingTray.exe [options]\r\n" +
               "\r\n" +
               "  -t <host>, --target=<host>   host to check (default 1.1.1.1)\r\n" +
               "  -i <sec>,  --interval=<sec>  seconds between checks (default 5)\r\n" +
               "  --method=<icmp|tcp|udp>      ping (default), a TCP connection, or a DNS query\r\n" +
               "  --port=<n>                   port for tcp (default 443) or udp (default 53)\r\n" +
               "  --orange=<ms>                a reply this slow or slower is orange (default 50)\r\n" +
               "  --red=<ms>                   a reply this slow or slower is red (default 150)\r\n" +
               "  -h, --help                   show this help and exit\r\n" +
               "  -v, --version                show version info and exit\r\n" +
               "\r\n" +
               "With no options, the settings last saved from the tray's\r\n" +
               "Settings dialog (settings.ini next to the exe; private to the\r\n" +
               "app in the Microsoft Store build) are used.\r\n" +
               "Command-line options always win and are never persisted.\r\n" +
               "One instance per target and method; others run side by side.";
    }

    private static string VersionText()
    {
        return "PingTray " + Version + " (" + VersionDate + ")\r\n" +
               "Live ping latency in the system tray.\r\n" +
               Author + " <" + AuthorEmail + ">";
    }

    // Compiled as /target:winexe, so there is no console by default. When
    // run from a terminal, attach to it so -h/-v behave like a normal CLI
    // tool; when stdout is a pipe/file, the inherited handle already works.
    // Only if neither exists (double-clicked with a flag) show a dialog.
    private static void PrintOrShow(string text)
    {
        bool attached = AttachConsole(ATTACH_PARENT_PROCESS);
        IntPtr stdout = GetStdHandle(STD_OUTPUT_HANDLE);

        if (attached || (stdout != IntPtr.Zero && stdout != new IntPtr(-1)))
        {
            try
            {
                using (var writer = new StreamWriter(Console.OpenStandardOutput()))
                {
                    // When attached to an interactive console the shell's
                    // prompt has already been printed; start on a fresh line.
                    if (attached)
                    {
                        writer.WriteLine();
                    }
                    writer.WriteLine(text);
                }

                if (attached && GetFileType(GetStdHandle(STD_OUTPUT_HANDLE)) == FILE_TYPE_CHAR)
                {
                    // The shell isn't waiting on this process (GUI
                    // subsystem), so its prompt printed before our output
                    // and the console now sits on a bare line that looks
                    // hung. Feed one Enter into the console input buffer
                    // so the shell redraws its prompt immediately.
                    var rec = new INPUT_RECORD();
                    rec.EventType = KEY_EVENT;
                    rec.KeyEvent.bKeyDown = true;
                    rec.KeyEvent.wRepeatCount = 1;
                    rec.KeyEvent.wVirtualKeyCode = VK_RETURN;
                    rec.KeyEvent.UnicodeChar = '\r';
                    uint written;
                    WriteConsoleInput(GetStdHandle(STD_INPUT_HANDLE), new[] { rec }, 1, out written);
                }
                return;
            }
            catch
            {
                // Console/pipe write failed (e.g. the reader closed the
                // pipe early). We were in a CLI context, so exit quietly
                // rather than popping a dialog.
            }
            return;
        }

        MessageBox.Show(text, "PingTray " + Version, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // The dialogs are modal, but the tray icon and its menu keep answering while one is open, so a second
    // click used to stack another copy on top of the first: they open at the same spot, so it looked like
    // one dialog until the top one was closed. Bring the open one forward instead. Returns true if a
    // dialog with this name was already open. A dialog opened from inside another (About from the menu
    // while Settings is open) sits above it and the first one is disabled, so the one to bring forward
    // is the one opened last - the one that takes input - not the dialog that was asked for.
    private static bool ActivateOpenDialog(string name)
    {
        Form top = DialogToActivate(name);
        if (top == null) return false;
        if (top.WindowState == FormWindowState.Minimized) top.WindowState = FormWindowState.Normal;
        top.Activate();
        return true;
    }

    // The open dialog a request for the dialog called name should bring forward: the innermost one if a
    // dialog of that name is already open, otherwise null (the caller creates it).
    private static Form DialogToActivate(string name)
    {
        bool isOpen = false;
        foreach (Form f in Application.OpenForms)
        {
            if (f.Name == name) isOpen = true;
        }
        return isOpen ? Application.OpenForms[Application.OpenForms.Count - 1] : null;
    }

    // Deliberately not a MessageBox: any MessageBox with an icon plays the
    // system notification sound, which is jarring for a plain About box.
    private static void ShowAboutDialog()
    {
        if (ActivateOpenDialog("about")) return;
        using (var form = new Form())
        {
            form.Name = "about";
            form.Text = "About PingTray";
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.ShowInTaskbar = false;
            form.ClientSize = new Size(330, 185);

            var iconBox = new PictureBox
            {
                Left = 15, Top = 18, Width = 32, Height = 32,
                SizeMode = PictureBoxSizeMode.StretchImage,
                Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap()
            };

            var label = new Label
            {
                Left = 62, Top = 15, Width = 255, Height = 125,
                Text = "PingTray " + Version + " (" + VersionDate + ")\n\n" +
                       "Live ping latency in the system tray.\n\n" +
                       Author + "\n" + AuthorEmail + "\n\n" +
                       "Command-line options: PingTray.exe -h"
            };

            var okButton = new Button { Text = "OK", Left = 235, Top = 150, Width = 80, DialogResult = DialogResult.OK };

            form.Controls.Add(iconBox);
            form.Controls.Add(label);
            form.Controls.Add(okButton);
            form.AcceptButton = okButton;
            form.CancelButton = okButton;
            form.ShowDialog();
        }
    }

    private static void LoadSettings()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;

            foreach (string rawLine in File.ReadAllLines(ConfigPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                int idx = line.IndexOf('=');
                if (idx < 0) continue;

                string key = line.Substring(0, idx).Trim().ToLowerInvariant();
                string value = line.Substring(idx + 1).Trim();

                if (key == "target" && value.Length > 0)
                {
                    // A hand-edited host that is not valid (see TargetProblem) is ignored like any other bad value.
                    if (TargetProblem(value) == null) Target = value;
                }
                else if (key == "interval")
                {
                    int seconds;
                    if (TryInterval(value, out seconds))
                    {
                        IntervalMs = seconds * 1000;
                    }
                }
                else if (key == "orange")
                {
                    int ms;
                    if (TryMs(value, out ms)) OrangeMs = ms;
                }
                else if (key == "red")
                {
                    int ms;
                    if (TryMs(value, out ms)) RedMs = ms;
                }
                else if (key == "method")
                {
                    Method m;
                    if (TryMethod(value, out m)) ProbeMethod = m;
                }
                else if (key == "tcpport" || key == "port")
                {
                    int port;
                    if (TryPort(value, out port)) TcpPort = port;
                }
                else if (key == "udpport")
                {
                    int port;
                    if (TryPort(value, out port)) UdpPort = port;
                }
            }

            // A file that puts red before orange (hand-edited) falls back to the defaults.
            if (RedMs <= OrangeMs)
            {
                OrangeMs = DefaultOrangeMs;
                RedMs = DefaultRedMs;
            }
        }
        catch
        {
            // Malformed or unreadable config: keep whatever was already resolved.
        }
    }

    private static void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllLines(ConfigPath, new[]
            {
                "target=" + Target,
                "interval=" + (IntervalMs / 1000),
                "orange=" + OrangeMs,
                "red=" + RedMs,
                "method=" + MethodKey(ProbeMethod),
                "tcpport=" + TcpPort,
                "udpport=" + UdpPort
            });
        }
        catch
        {
            // Best-effort; not critical if this fails (e.g. read-only folder).
        }
    }

    private static void ShowSettingsDialog()
    {
        if (ActivateOpenDialog("settings")) return;
        using (var form = new Form())
        {
            form.Name = "settings";
            form.Text = "Ping Tray Settings";
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.ClientSize = new Size(305, 400);
            // Every input is 160 px wide: the widest drop-down item ("Wi-Fi / broadband (default)") needs about 157 px to show in full, so this is as
            // narrow as they can all go together. The form keeps 15 px either side of the labels and boxes, and the hint (four lines at this width) fits.

            var targetLabel = new Label { Text = "Target host:", Left = 15, Top = 20, Width = 110 };
            var targetBox = new TextBox { Name = "targetBox", Text = Target, Left = 130, Top = 17, Width = 160 };

            var methodLabel = new Label { Text = "Method:", Left = 15, Top = 55, Width = 110 };
            var methodBox = new ComboBox { Left = 130, Top = 52, Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
            methodBox.Items.AddRange(MethodNames);
            methodBox.SelectedIndex = (int)ProbeMethod;

            // The port belongs to TCP connect (443 unless changed) and to the DNS query (53), each
            // remembered on its own: the box shows the one for the chosen method and is greyed out for ping.
            string[] portText = { "", TcpPort.ToString(), UdpPort.ToString() };
            int portShown = (int)ProbeMethod;
            var portLabel = new Label { Text = "Port:", Left = 15, Top = 90, Width = 110 };
            var portBox = new TextBox { Name = "portBox", Text = portShown > 0 ? portText[portShown] : portText[1], Left = 130, Top = 87, Width = 160, Enabled = portShown > 0 };
            methodBox.SelectedIndexChanged += (s, e) =>
            {
                if (portShown > 0) portText[portShown] = portBox.Text;   // keep what was typed for the method being left
                portShown = methodBox.SelectedIndex;
                portBox.Enabled = portShown > 0;
                if (portShown > 0) portBox.Text = portText[portShown];
            };

            var intervalLabel = new Label { Text = "Interval (sec):", Left = 15, Top = 125, Width = 110 };
            var intervalBox = new TextBox { Name = "intervalBox", Text = (IntervalMs / 1000).ToString(), Left = 130, Top = 122, Width = 160 };

            var typeLabel = new Label { Text = "Connection type:", Left = 15, Top = 160, Width = 110 };
            var typeBox = new ComboBox { Left = 130, Top = 157, Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
            typeBox.Items.AddRange(PresetNames);
            typeBox.Items.Add("Custom");
            typeBox.SelectedIndex = PresetIndex(OrangeMs, RedMs);

            var orangeLabel = new Label { Text = "Orange from (ms):", Left = 15, Top = 195, Width = 110 };
            var orangeBox = new TextBox { Name = "orangeBox", Text = OrangeMs.ToString(), Left = 130, Top = 192, Width = 160 };
            var redLabel = new Label { Text = "Red from (ms):", Left = 15, Top = 230, Width = 110 };
            var redBox = new TextBox { Name = "redBox", Text = RedMs.ToString(), Left = 130, Top = 227, Width = 160 };

            // Start at login. The unzipped build owns this (a Run-key entry, applied with OK); in the Store
            // build Windows owns it, so there is a link to the page where it is switched instead.
            bool packaged = IsPackaged();
            Control startupControl;
            CheckBox startupBox = null;
            if (packaged)
            {
                var startupLink = new LinkLabel { Text = "Start with Windows: change in Windows Settings", Left = 15, Top = 264, Width = 275, Height = 18 };
                startupLink.LinkClicked += (s, e) =>
                {
                    try { Process.Start("ms-settings:startupapps"); }
                    catch { }
                };
                startupControl = startupLink;
            }
            else
            {
                startupBox = new CheckBox { Text = "Start with Windows", Left = 15, Top = 262, Width = 275, Checked = GetRunAtLogin() };
                startupControl = startupBox;
            }

            // The hint, which turns into the message when OK finds something wrong (see FormClosing below).
            var hint = new Label
            {
                Name = "hint",
                Text = "Pick a connection type for suggested limits, or type your own. Replies faster than the orange limit are green. " +
                       "If ping is blocked on your network, try TCP connect or a DNS query.",
                Left = 15, Top = 292, Width = 275, Height = 56, ForeColor = SystemColors.GrayText
            };

            var okButton = new Button { Text = "OK", Left = 115, Top = 360, Width = 80, DialogResult = DialogResult.OK };
            var cancelButton = new Button { Text = "Cancel", Left = 210, Top = 360, Width = 80, DialogResult = DialogResult.Cancel };

            // The suggestions and the two boxes follow each other: choosing a connection type fills in
            // its limits, and typing limits that match a type (or no type) updates the drop-down.
            bool syncing = false;
            typeBox.SelectedIndexChanged += (s, e) =>
            {
                if (syncing || typeBox.SelectedIndex >= PresetNames.Length) return;
                syncing = true;
                orangeBox.Text = PresetOrange[typeBox.SelectedIndex].ToString();
                redBox.Text = PresetRed[typeBox.SelectedIndex].ToString();
                syncing = false;
            };
            EventHandler limitsTyped = (s, e) =>
            {
                if (syncing) return;
                int o, r;
                syncing = true;
                typeBox.SelectedIndex = (int.TryParse(orangeBox.Text.Trim(), out o) && int.TryParse(redBox.Text.Trim(), out r))
                    ? PresetIndex(o, r) : PresetNames.Length;
                syncing = false;
            };
            orangeBox.TextChanged += limitsTyped;
            redBox.TextChanged += limitsTyped;

            form.Controls.Add(targetLabel);
            form.Controls.Add(targetBox);
            form.Controls.Add(methodLabel);
            form.Controls.Add(methodBox);
            form.Controls.Add(portLabel);
            form.Controls.Add(portBox);
            form.Controls.Add(intervalLabel);
            form.Controls.Add(intervalBox);
            form.Controls.Add(typeLabel);
            form.Controls.Add(typeBox);
            form.Controls.Add(orangeLabel);
            form.Controls.Add(orangeBox);
            form.Controls.Add(redLabel);
            form.Controls.Add(redBox);
            form.Controls.Add(startupControl);
            form.Controls.Add(hint);
            form.Controls.Add(okButton);
            form.Controls.Add(cancelButton);
            form.AcceptButton = okButton;
            form.CancelButton = cancelButton;

            // OK is checked before the dialog closes: if something is wrong the dialog stays open, says what
            // in the hint's place (in red) and puts the cursor in that box. (The first v1.5 build closed the
            // dialog first, threw away everything typed and showed one message listing every rule.) Editing
            // any box brings the hint back.
            string hintText = hint.Text;
            form.FormClosing += (s, e) =>
            {
                if (form.DialogResult != DialogResult.OK) return;
                if (portShown > 0) portText[portShown] = portBox.Text;
                int field;
                string problem = SettingsProblem(targetBox.Text, methodBox.SelectedIndex > 0, portText[methodBox.SelectedIndex],
                    intervalBox.Text, orangeBox.Text, redBox.Text, out field);
                if (problem == null) return;
                e.Cancel = true;
                hint.Text = problem;
                hint.ForeColor = Color.Firebrick;
                TextBox bad = field == 0 ? targetBox : (field == 1 ? portBox : (field == 2 ? intervalBox : (field == 3 ? orangeBox : redBox)));
                bad.Focus();
                bad.SelectAll();
            };
            EventHandler edited = (s, e) =>
            {
                hint.Text = hintText;
                hint.ForeColor = SystemColors.GrayText;
            };
            foreach (TextBox box in new[] { targetBox, portBox, intervalBox, orangeBox, redBox }) box.TextChanged += edited;

            if (form.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            // Only an OK with nothing wrong gets here (FormClosing above), so all of this parses; the
            // check is a guard, not a second set of rules.
            string newTarget = targetBox.Text.Trim();
            Method newMethod = (Method)methodBox.SelectedIndex;
            if (portShown > 0) portText[portShown] = portBox.Text;
            // The chosen method's port was checked; the other method's port keeps its old value
            // if what is in its slot is not valid (the box for ping is greyed out and ignored).
            int newTcpPort = TcpPort, newUdpPort = UdpPort;
            AdoptPort(portText[1], ref newTcpPort);
            AdoptPort(portText[2], ref newUdpPort);
            int newIntervalSeconds, newOrange, newRed;
            if (!TryInterval(intervalBox.Text, out newIntervalSeconds) || !TryMs(orangeBox.Text, out newOrange) ||
                !TryMs(redBox.Text, out newRed) || newRed <= newOrange)
            {
                return;
            }

            // A different host, way of measuring or port starts the offline count, the "another method
            // works" hint and the recent readings afresh.
            int newPort = newMethod == Method.Udp ? newUdpPort : newTcpPort;
            if (newTarget != Target || newMethod != ProbeMethod || (newMethod != Method.Icmp && newPort != PortFor(newMethod)))
            {
                _consecutiveFailures = 0;
                _lastGoodMs = -1;
                _otherWorking = -1;
                _recent.Clear();
            }

            Target = newTarget;
            ProbeMethod = newMethod;
            TcpPort = newTcpPort;
            UdpPort = newUdpPort;
            IntervalMs = newIntervalSeconds * 1000;
            OrangeMs = newOrange;
            RedMs = newRed;
            _timer.Interval = IntervalMs;
            if (startupBox != null) SetRunAtLogin(startupBox.Checked);
            SaveSettings();
            UpdatePing();
        }
    }

    // The most characters the shell shows in a tray tooltip.
    private const int ShellTipMax = 127;

    private static void SetTooltip(string text)
    {
        // NotifyIcon.Text throws if length >= 64 (each newline counts as one) although the
        // shell takes 127, and the target host is user-editable (Settings dialog /
        // --target), so length isn't predictable at the call sites. Up to 63 characters use
        // the public setter. Longer text (the multi-line ping tooltip) sets the private
        // field and refreshes the icon, the usual workaround, capped at the shell's 127;
        // if that ever fails, fall back to truncating to 63.
        if (text.Length <= 63)
        {
            _notifyIcon.Text = text;
            return;
        }
        if (text.Length > ShellTipMax)
        {
            text = text.Substring(0, ShellTipMax - 3) + "...";
        }
        try
        {
            typeof(NotifyIcon).GetField("text", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_notifyIcon, text);
            if ((bool)typeof(NotifyIcon).GetField("added", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_notifyIcon))
            {
                typeof(NotifyIcon).GetMethod("UpdateIcon", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_notifyIcon, new object[] { true });
            }
        }
        catch
        {
            _notifyIcon.Text = text.Substring(0, 60) + "...";
        }
    }

    // The colour for a reply of this many ms, by the current limits.
    private static Tone ToneFor(long ms)
    {
        return ms < OrangeMs ? Tone.Good : (ms < RedMs ? Tone.Warn : Tone.Bad);
    }

    // "Preset: Wi-Fi (<50 / 50-150 / ≥150)": the connection type the limits match ("Custom" if none)
    // and the limits in force. The spaces inside the brackets are no-break spaces, so if the shell
    // has to wrap the line it breaks before the brackets instead of through the numbers.
    private static string RangeLegend()
    {
        int p = PresetIndex(OrangeMs, RedMs);
        string name = p < PresetShort.Length ? PresetShort[p] : "Custom";
        string nb = ((char)0xA0).ToString();
        return string.Format("Preset: {0} (<{1}{3}/{3}{1}-{2}{3}/{3}≥{2})", name, OrangeMs, RedMs, nb);
    }

    // True for a reply that is quicker than the preset's kind of connection ever manages (see
    // PresetFast): a 22 ms reply on the "4G" preset means the connection is not 4G. Never for Custom
    // limits, and never for the types with no floor (LAN, Wired, Wi-Fi: a fast reply is always normal).
    private static bool FasterThanExpected(long ms)
    {
        int p = PresetIndex(OrangeMs, RedMs);
        return p < PresetFast.Length && ms < PresetFast[p];
    }

    // Tooltip for a good reply: the reading, the colour limits in force (the legend line, which names the
    // preset - so the verdict does not need to), and a plain verdict against the preset - "Running as
    // expected" while green, "Faster than expected" when the reply is quicker than that kind of connection
    // manages, "Slower than expected" or "Much slower than expected" when orange or red. Whenever the
    // reply is not as expected, in either direction, it also gets the tongue-in-cheek ready reckoner,
    // which says what kind of connection a ping like that feels like. The shell cuts anything past
    // ShellTipMax, so when the full wording does not fit (a long host name, or the note below) the
    // reckoner line goes, rather than the shell cutting a line off mid-word.
    //
    // ms is the latest reading, judged is the value the wording is judged on (the median of the last few
    // readings, see Judge; the same as ms until there are enough). When the two read differently - the
    // latest reading alone would have got other wording - the first line says what the wording is based
    // on: "Ping to 1.1.1.1: 41 ms (usually 22 ms)". That note is the last thing to go when space is short.
    private static string SuccessTip(string label, long ms, long judged)
    {
        string like, likeNow;
        string verdict = Verdict(judged, out like);
        bool differs = ms != judged && (Verdict(ms, out likeNow) != verdict || likeNow != like);
        string reading = label + ": " + ms + " ms";
        string legend = RangeLegend();
        string head = reading + (differs ? " (usually " + judged + " ms)" : "") + "\n" + legend + "\n";
        string full = head + verdict + (like == null ? "" : "\n" + like);
        if (full.Length <= ShellTipMax) return full;
        full = head + verdict;
        if (full.Length <= ShellTipMax || !differs) return full;
        return reading + "\n" + legend + "\n" + verdict;
    }

    // The plain verdict for a reading against the limits in force and, unless it is as expected, the
    // reckoner line ("Like 4G from 2009-present"); like is null when there is none.
    private static string Verdict(long ms, out string like)
    {
        Tone tone = ToneFor(ms);
        bool faster = tone == Tone.Good && FasterThanExpected(ms);
        if (tone == Tone.Good && !faster)
        {
            like = null;
            return "Running as expected";
        }
        like = "Like " + Reckoner(ms);
        return faster ? "Faster than expected" : (tone == Tone.Warn ? "Slower than expected" : "Much slower than expected");
    }

    // Takes in a good reading and returns the value the tooltip's wording is judged on: the median of the
    // last MedianWindow readings, but only those taken within MedianMaxAge. The median needs an odd
    // number of readings to be one of them, so with two or four the oldest is left out: the first
    // reading, and the second, are judged as they are, the third on three, the fifth on five.
    // One reading that is far out then cannot change the wording; it takes three of five.
    private static long Judge(long ms, DateTime at)
    {
        _recent.RemoveAll(s => s.At > at || at - s.At > MedianMaxAge);   // too old, or from a clock that has since moved back
        _recent.Add(new Sample { Ms = ms, At = at });
        while (_recent.Count > MedianWindow) _recent.RemoveAt(0);
        int n = _recent.Count;
        if (n % 2 == 0) n--;
        var middle = new List<long>();
        for (int i = _recent.Count - n; i < _recent.Count; i++) middle.Add(_recent[i].Ms);
        middle.Sort();
        return middle[n / 2];
    }

    // Suggested colour limits per connection type (orange from / red from, in ms), shown in the
    // Settings dialog. Typing limits that match one selects it; anything else reads "Custom".
    private static readonly string[] PresetNames = {
        "LAN (router or server)", "Wired broadband / fibre", "Wi-Fi / broadband (default)",
        "5G", "4G", "Satellite (geostationary)", "90s dial-up" };
    private static readonly int[] PresetOrange = { 5, 20, 50, 60, 80, 800, 250 };
    private static readonly int[] PresetRed = { 20, 60, 150, 120, 200, 1200, 500 };
    // Below this many ms a reply is "Faster than expected" for the type: quicker than that kind of
    // connection can manage, so the connection is probably something else (22 ms with the 4G preset
    // while on Wi-Fi, say). 0 = no floor, because any fast reply is normal on a LAN, a wired link or
    // Wi-Fi. Estimates, set just under the best a GENUINE connection of the kind reads, so a real one is
    // not flagged: 5G is typically 15-40 ms and about 12 at best (10), 4G typically 40-70 with good metro
    // LTE at 25-35 (25), a geostationary satellite cannot beat about 480 ms (450), dial-up modems
    // typically 100-220 ms and about 90 at best (90).
    private static readonly int[] PresetFast = { 0, 0, 0, 10, 25, 450, 90 };
    // The same seven types in a few characters, for the tooltip's "Preset: ..." line.
    private static readonly string[] PresetShort = { "LAN", "Wired", "Wi-Fi", "5G", "4G", "Satellite", "Dial-up" };

    // Index into PresetNames for these limits, or PresetNames.Length ("Custom") if none matches.
    private static int PresetIndex(int orange, int red)
    {
        for (int i = 0; i < PresetNames.Length; i++)
        {
            if (PresetOrange[i] == orange && PresetRed[i] == red) return i;
        }
        return PresetNames.Length;
    }

    // Ready reckoner (tongue-in-cheek): which kind of connection a ping like this feels like, and the
    // year that technology debuted. It is only shown for a reply that is slower than the preset expects,
    // and the bands are laid out so that a wired link going slow reads "Wi-Fi" first, then "5G", then
    // "4G": a reply under ReckonUnder[i] ms feels like ReckonName[i] (so 4-24 ms is Wi-Fi 6, 25-46 is
    // 5G, and so on); at or over the last edge it is the last rung. Order of magnitude only - not a
    // measurement of anything.
    private static readonly int[] ReckonUnder = { 4, 25, 47, 80, 137, 210, 330, 580, 1095 };
    private static readonly string[] ReckonName = {
        "gigabit Ethernet", "Wi-Fi 6", "5G", "4G", "3G", "56K dial-up",
        "28.8K dial-up", "14.4K dial-up", "a 2400-baud modem", "ARPANET" };
    // "<year>-present" for what is still in everyday use (a non-breaking hyphen, so a wrap cannot
    // split it); the debut year alone for what has had its day.
    private static readonly string[] ReckonYear = {
        "1999\u2011present", "2019\u2011present", "2019\u2011present", "2009\u2011present",
        "2001", "1997", "1994", "1991", "1984", "1969" };

    private static string Reckoner(long ms)
    {
        int i = 0;
        while (i < ReckonUnder.Length && ms >= ReckonUnder[i]) i++;
        // A no-break space keeps "from <year>" together if the shell has to wrap the line.
        return ReckonName[i] + " from" + ((char)0xA0).ToString() + ReckonYear[i];
    }

    // Guards against overlapping attempts: a timed-out attempt (up to 2s) can
    // outlive a short interval's next tick, and Ping throws if a send is
    // started while one is pending. Skipped ticks just wait for the next.
    private static bool _pingInFlight;

    // One reading: the time in ms, or a short reason it failed.
    private struct Reading
    {
        public bool Ok;
        public long Ms;
        public string Reason;

        public static Reading Success(long ms) { return new Reading { Ok = true, Ms = ms }; }
        public static Reading Fail(string reason) { return new Reading { Ok = false, Reason = reason }; }
    }

    // What a tooltip says it measured: "Ping to 1.1.1.1", "TCP to 1.1.1.1:443" or "DNS to 1.1.1.1:53".
    private static string WhatLabel(Method method, string target, int port)
    {
        if (method == Method.Tcp) return "TCP to " + target + ":" + port;
        if (method == Method.Udp) return "DNS to " + target + ":" + port;
        return "Ping to " + target;
    }

    // Ping, TCP or DNS: the method as the tooltip names it.
    private static string ShortName(Method method)
    {
        return method == Method.Tcp ? "TCP" : (method == Method.Udp ? "DNS" : "Ping");
    }

    // .NET Framework's name lookup does not convert a name with accents or non-English letters to its
    // xn-- form (punycode), so such a name (an umlaut in a German domain, say) is never found. Do the
    // conversion before a name is handed to the system. ASCII text (every address and ordinary name) is
    // returned as it is, and so is anything that cannot be converted (the lookup then fails as it always did).
    private static string AsciiHost(string host)
    {
        foreach (char c in host)
        {
            if (c > 127)
            {
                try { return new System.Globalization.IdnMapping().GetAscii(host); }
                catch (ArgumentException) { return host; }
            }
        }
        return host;
    }

    private static Task<Reading> MeasureAsync(Method method, string target, int port)
    {
        target = AsciiHost(target);
        if (method == Method.Tcp) return TcpAsync(target, port);
        if (method == Method.Udp) return UdpAsync(target, port);
        return IcmpAsync(target);
    }

    // An ICMP echo. Failure reasons are the IPStatus names; TimedOut is what a firewall that
    // drops ping looks like.
    private static async Task<Reading> IcmpAsync(string target)
    {
        try
        {
            PingReply reply = await _ping.SendPingAsync(target, PingTimeoutMs);
            return reply.Status == IPStatus.Success
                ? Reading.Success(reply.RoundtripTime)
                : Reading.Fail(reply.Status.ToString());
        }
        catch (Exception ex)
        {
            return Reading.Fail("error: " + ex.GetType().Name);
        }
    }

    // We have stopped waiting for this task; if it fails later (a connect cut short by closing its
    // socket), look at the failure so it is not left unobserved.
    private static void Observe(Task task)
    {
        task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }

    // The address to measure: a literal address is used as it is, a name is looked up (IPv4
    // preferred, 2 s cap); null means the lookup failed. The lookup is never part of a reading's time.
    private static async Task<IPAddress> ResolveAsync(string host)
    {
        IPAddress ip;
        if (IPAddress.TryParse(host, out ip))
        {
            return ip;
        }

        Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(host);
        if (await Task.WhenAny(lookup, Task.Delay(PingTimeoutMs)) != lookup)
        {
            Observe(lookup);
            return null;
        }
        IPAddress[] found;
        try
        {
            found = await lookup;
        }
        catch (SocketException)
        {
            return null;
        }
        foreach (IPAddress a in found)
        {
            if (a.AddressFamily == AddressFamily.InterNetwork) return a;
        }
        return found.Length > 0 ? found[0] : null;
    }

    // The time a TCP connection takes to open: the handshake is one round trip, so it reads about
    // the same as a ping (a few ms more) and it works where ICMP is blocked. The name is looked up
    // first and is not part of the time (a ping's round trip leaves it out too). Nothing is sent
    // but the handshake. A closed port is a failure like any other: Windows only reports the reset
    // after about two seconds of SYN retries, so its "time" would not be a round trip anyway.
    private static async Task<Reading> TcpAsync(string host, int port)
    {
        try
        {
            IPAddress ip = await ResolveAsync(host);
            if (ip == null) return Reading.Fail("DnsFailed");

            using (var client = new TcpClient(ip.AddressFamily))
            {
                var clock = Stopwatch.StartNew();
                Task connect = client.ConnectAsync(ip, port);
                if (await Task.WhenAny(connect, Task.Delay(PingTimeoutMs)) != connect)
                {
                    // Closing the client at the end of this block fails the pending connect.
                    Observe(connect);
                    return Reading.Fail("TimedOut");
                }
                try
                {
                    await connect;
                }
                catch (SocketException ex)
                {
                    return Reading.Fail(ex.SocketErrorCode.ToString());
                }
                clock.Stop();
                return Reading.Success((long)Math.Round(clock.Elapsed.TotalMilliseconds));
            }
        }
        catch (Exception ex)
        {
            return Reading.Fail("error: " + ex.GetType().Name);
        }
    }

    private static readonly Random DnsIds = new Random();

    // The time one DNS query takes to be answered over UDP. UDP has no handshake to time, so this
    // needs a service that answers; DNS on port 53 does. The query asks for the root name servers'
    // list, which every DNS server can answer (a resolver from its own memory, so it depends neither
    // on its cache nor on the internet). Any well-formed reply counts, even an error: the round
    // trip was real. The name is looked up first and is not part of the time.
    private static async Task<Reading> UdpAsync(string host, int port)
    {
        try
        {
            IPAddress ip = await ResolveAsync(host);
            if (ip == null) return Reading.Fail("DnsFailed");

            int id = DnsIds.Next(1, 65536);
            byte[] query = new byte[]
            {
                (byte)(id >> 8), (byte)id, 0x01, 0x00,                 // id; a standard query, recursion desired
                0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,        // one question, nothing else
                0x00, 0x00, 0x02, 0x00, 0x01                           // the root name, type NS, class IN
            };

            using (var client = new UdpClient(ip.AddressFamily))
            {
                client.Connect(ip, port);   // only this server's answers (and its "port closed" error) get through
                var clock = Stopwatch.StartNew();
                Task<UdpReceiveResult> receive;
                try
                {
                    await client.SendAsync(query, query.Length);
                    receive = client.ReceiveAsync();   // can fail at once if the port just answered "closed"
                }
                catch (SocketException ex)
                {
                    return Reading.Fail(ex.SocketErrorCode.ToString());
                }
                if (await Task.WhenAny(receive, Task.Delay(PingTimeoutMs)) != receive)
                {
                    // Closing the client at the end of this block fails the pending receive.
                    Observe(receive);
                    return Reading.Fail("TimedOut");
                }
                UdpReceiveResult reply;
                try
                {
                    reply = await receive;
                }
                catch (SocketException ex)
                {
                    return Reading.Fail(ex.SocketErrorCode.ToString());
                }
                clock.Stop();

                byte[] data = reply.Buffer;
                if (data.Length < 12 || data[0] != query[0] || data[1] != query[1] || (data[2] & 0x80) == 0)
                {
                    return Reading.Fail("BadReply");   // not an answer to our query
                }
                return Reading.Success((long)Math.Round(clock.Elapsed.TotalMilliseconds));
            }
        }
        catch (Exception ex)
        {
            return Reading.Fail("error: " + ex.GetType().Name);
        }
    }

    // Offline, but another way of measuring still gets through: that is what a firewall blocking
    // one of them looks like. Say so, and where to switch.
    private static string OtherMethodHint(Method current, Method works)
    {
        return "\n" + ShortName(current) + " looks blocked, but " + (works == Method.Icmp ? "ping" : ShortName(works)) + " works." +
            "\nSwitch it in Settings > Method.";
    }

    // async void is fine here: it's a top-level event-driven handler, all
    // exceptions are caught inside, and the WinForms SynchronizationContext
    // resumes us on the UI thread, so a reading never blocks the message
    // pump (menu/tooltip stay responsive while one times out).
    private static async void UpdatePing()
    {
        if (_pingInFlight)
        {
            return;
        }
        _pingInFlight = true;

        // Snapshots: the Settings dialog can retarget or switch the method while this attempt is
        // awaiting; label the result with what was actually measured.
        string target = Target;
        Method method = ProbeMethod;
        int port = PortFor(method);

        Reading reading = Reading.Fail("error");
        bool probed = false;
        int working = -1;

        try
        {
            reading = await MeasureAsync(method, target, port);

            // Third miss in a row (and not a failed name lookup): does ANOTHER method get through?
            // If so it is a firewall blocking one of them, not an outage, and the tooltip says which
            // to use. The other two are tried at once and the first that works (ping, then TCP, then
            // DNS) is remembered. They are tried again on every further offline tick, so the hint
            // goes as soon as nothing gets through any more (a working method answers in a few ms;
            // when all fail the extra wait is one timeout).
            if (!reading.Ok && _consecutiveFailures + 1 >= FailureThreshold &&
                reading.Reason != "DnsFailed" && !reading.Reason.StartsWith("error:"))
            {
                var others = new List<Method>();
                var probes = new List<Task<Reading>>();
                foreach (Method m in new[] { Method.Icmp, Method.Tcp, Method.Udp })
                {
                    if (m == method) continue;
                    others.Add(m);
                    probes.Add(MeasureAsync(m, target, PortFor(m)));
                }
                await Task.WhenAll(probes);
                for (int i = 0; i < others.Count; i++)
                {
                    if (probes[i].Result.Ok) { working = (int)others[i]; break; }
                }
                probed = true;
            }
        }
        finally
        {
            _pingInFlight = false;
        }

        // Only trust the probe if nothing was changed in Settings while it ran.
        if (probed && target == Target && method == ProbeMethod && port == PortFor(method))
        {
            _otherWorking = working;
        }

        if (reading.Ok)
        {
            ShowReading(method, target, port, reading.Ms);
            return;
        }

        _consecutiveFailures++;

        if (_consecutiveFailures >= FailureThreshold)
        {
            // After an outage the connection is judged afresh, not against readings from before it.
            _recent.Clear();
            SetTooltip(string.Format("{0}: offline ({1}) [{2}x]", WhatLabel(method, target, port), reading.Reason, _consecutiveFailures) +
                (_otherWorking >= 0 ? OtherMethodHint(method, (Method)_otherWorking) : ""));
            ApplyIcon("0", Tone.Offline);
        }
        else
        {
            // Below threshold: treat as a transient blip. Leave the icon
            // showing the last known-good reading; only the tooltip
            // reflects that attempts are currently failing.
            string lastGoodSuffix = _lastGoodMs >= 0 ? " (last: " + _lastGoodMs + "ms)" : "";
            SetTooltip(string.Format("{0}: no response {1}/{2}{3}", WhatLabel(method, target, port), _consecutiveFailures, FailureThreshold, lastGoodSuffix));
        }
    }

    // A good reading: it ends any run of misses, and the icon shows it as it is while the tooltip's
    // wording is judged on the median of the recent ones (see Judge).
    private static void ShowReading(Method method, string target, int port, long ms)
    {
        _consecutiveFailures = 0;
        _otherWorking = -1;
        _lastGoodMs = ms;

        string text = ms >= 1000 ? "999+" : ms.ToString();

        // A reading for the host and method now being watched joins the recent ones; one that was
        // still on its way when Settings changed them is shown as it is and not remembered. (A ping has
        // no port, so only TCP and DNS readings care that theirs is still the port in use.)
        bool current = target == Target && method == ProbeMethod && (method == Method.Icmp || port == PortFor(method));
        long judged = current ? Judge(ms, DateTime.UtcNow) : ms;

        SetTooltip(SuccessTip(WhatLabel(method, target, port), ms, judged));
        ApplyIcon(text, ToneFor(ms));
    }

    // What the number says. Each tone has a bright colour for a dark taskbar and a darker one
    // for a light taskbar (see ToneColor); Offline is a plain grey on both, with no outline.
    private enum Tone { Good, Warn, Bad, Offline }

    private static Color ToneColor(Tone tone, bool light)
    {
        switch (tone)
        {
            case Tone.Good: return light ? Color.FromArgb(0, 130, 0) : Color.LimeGreen;
            case Tone.Warn: return light ? Color.FromArgb(205, 100, 0) : Color.Orange;
            // Bad: pure Color.Red was only 3.9:1 on a maroon accent taskbar; this lighter red is 5.0:1
            // there (4.1 -> 5.2 on Windows dark grey) and still clearly red.
            case Tone.Bad: return light ? Color.FromArgb(200, 0, 0) : Color.FromArgb(255, 85, 85);
            // Offline: a dull dark grey that still reads on both a dark and a light taskbar
            // (about 3:1 on dark ones, 4.6:1 on white), so one colour serves both themes.
            default: return Color.FromArgb(110, 110, 110);
        }
    }

    // True when the Windows taskbar / tray is using the light theme (the system theme, not the
    // apps theme). Dark when the setting is missing or unreadable.
    private static bool IsLightTaskbar()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = (k == null) ? null : k.GetValue("SystemUsesLightTheme");
                return v is int && (int)v == 1;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyIcon(string text, Tone tone)
    {
        // Read the theme on every redraw (cheap) so a Windows theme switch shows at the next ping.
        Icon newIcon = RenderIcon(text, tone, IsLightTaskbar());
        Icon oldIcon = _currentIcon;
        _currentIcon = newIcon;
        _notifyIcon.Icon = newIcon;

        if (oldIcon != null)
        {
            var h = oldIcon.Handle;
            oldIcon.Dispose();
            DestroyIcon(h);
        }
    }

    // The number as outlined text filling a 32x32 icon (Windows scales it into the tray slot).
    private static Icon RenderIcon(string text, Tone tone, bool light)
    {
        const int S = 32;
        Color color = ToneColor(tone, light);
        // The outline is the opposite of the text: dark on a dark taskbar, light on a light one.
        // The grey offline "0" gets none: it should look dull and inactive, not outlined.
        Color halo = tone == Tone.Offline ? Color.Transparent
            : (light ? Color.FromArgb(190, 255, 255, 255) : Color.FromArgb(190, 15, 15, 15));
        using (var bmp = new Bitmap(S, S))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                DrawFit(g, text, color, halo, new RectangleF(1, 2, S - 2, S - 4));
            }

            IntPtr hIcon = bmp.GetHicon();
            return Icon.FromHandle(hIcon);
        }
    }

    // Digits are never stretched taller than 1 / MinSqueeze of their natural shape; InkGap is the
    // space between characters, in the 100-unit em they are laid out in.
    private const float MinSqueeze = 0.85f;
    private const float InkGap = 9f;

    // The text as one outline path. Each character is placed by its ink, not by its advance
    // width, so a narrow "1" takes little room and "111" needs far less squeezing to fit.
    private static System.Drawing.Drawing2D.GraphicsPath TightText(string text)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        using (var family = new FontFamily("Segoe UI"))
        {
            float x = 0;
            foreach (char ch in text)
            {
                using (var glyph = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    glyph.AddString(ch.ToString(), family, (int)FontStyle.Bold, 100f, new PointF(0, 0), StringFormat.GenericTypographic);
                    RectangleF cb = glyph.GetBounds();
                    if (cb.Width <= 0) continue;
                    using (var m = new System.Drawing.Drawing2D.Matrix())
                    {
                        m.Translate(x - cb.X, 0);
                        glyph.Transform(m);
                    }
                    path.AddPath(glyph, false);
                    x += cb.Width + InkGap;
                }
            }
        }
        return path;
    }

    // Draws text as an outline path scaled to fill the box (digits as tall as the box allows),
    // with a thin halo (none if the halo colour is transparent) so it reads on any taskbar.
    private static void DrawFit(Graphics g, string text, Color color, Color halo, RectangleF box)
    {
        using (var path = TightText(text))
        {
            RectangleF b = path.GetBounds();
            if (b.Width <= 0 || b.Height <= 0) return;
            // Fill the box height; if the text is too wide, squeeze it horizontally (never
            // below MinSqueeze) and only then shrink it.
            float fitW = box.Width / b.Width, fitH = box.Height / b.Height;
            float sy = Math.Min(fitH, fitW / MinSqueeze);
            float sx = Math.Min(sy, fitW);
            using (var m = new System.Drawing.Drawing2D.Matrix())
            {
                m.Translate(box.X + box.Width / 2f, box.Y + box.Height / 2f);
                m.Scale(sx, sy);
                m.Translate(-(b.X + b.Width / 2f), -(b.Y + b.Height / 2f));
                path.Transform(m);
            }
            if (halo.A > 0)
            {
                using (var pen = new Pen(halo, 2f))
                {
                    pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    g.DrawPath(pen, path);
                }
            }
            using (var brush = new SolidBrush(color))
            {
                g.FillPath(brush, path);
            }
        }
    }
}
