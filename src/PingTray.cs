using Microsoft.Win32;
using System;
using System.Drawing;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
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

    internal const string Version = "1.4";
    private const string VersionDate = "2026-10-09";
    private const string Author = "Andrew Reardon";
    private const string AuthorEmail = "andrewreardon@gmail.com";

    private static string Target = "1.1.1.1";
    private static int IntervalMs = 5000;

    // Set by ParseArgs when the corresponding option was given explicitly.
    // Command-line options always win: if either was passed, settings.ini
    // is not loaded at all (defaults + flags only), and flags are never
    // persisted — only Settings-dialog edits write settings.ini.
    private static bool _targetFromCli;
    private static bool _intervalFromCli;

    // A single dropped ICMP reply is common on flaky links (e.g. 4G) and
    // isn't necessarily "down" — only flip to offline after this many
    // consecutive failed attempts. Each attempt waits up to PingTimeoutMs
    // before being counted as failed.
    private const int FailureThreshold = 3;
    private const int PingTimeoutMs = 2000;
    private static int _consecutiveFailures = 0;
    private static long _lastGoodMs = -1;

    private static string ConfigPath;

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

        ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");

        // No explicit options: resume whatever was being monitored last
        // time (saved by the Settings dialog). Any explicit option means
        // the command line fully describes this run — built-in defaults
        // plus the given flags; the saved file is ignored.
        if (!_targetFromCli && !_intervalFromCli)
        {
            LoadSettings();
        }

        // Prevent duplicate instances monitoring the same resolved target
        // (different targets are allowed to run side by side).
        bool createdNew;
        var mutex = new System.Threading.Mutex(true, "Global\\PingTray_SingleInstance_" + Target, out createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "PingTray is already running for " + Target + " — look for its icon in the tray (or under the ^ overflow arrow near the clock).\n\n" +
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
                if (value.Length > 0)
                {
                    Target = value;
                    _targetFromCli = true;
                }
            }
            else if (arg == "-t" && i + 1 < args.Length)
            {
                string value = args[++i].Trim();
                if (value.Length > 0)
                {
                    Target = value;
                    _targetFromCli = true;
                }
            }
            else if (arg.StartsWith("--interval=", StringComparison.OrdinalIgnoreCase) ||
                     arg.StartsWith("-i=", StringComparison.Ordinal))
            {
                int seconds;
                if (int.TryParse(arg.Substring(arg.IndexOf('=') + 1), out seconds) && seconds > 0)
                {
                    IntervalMs = seconds * 1000;
                    _intervalFromCli = true;
                }
            }
            else if (arg == "-i" && i + 1 < args.Length)
            {
                int seconds;
                if (int.TryParse(args[++i], out seconds) && seconds > 0)
                {
                    IntervalMs = seconds * 1000;
                    _intervalFromCli = true;
                }
            }
        }
        return true;
    }

    private static string UsageText()
    {
        return "PingTray " + Version + " - live ping latency in the system tray\r\n" +
               "\r\n" +
               "Usage: PingTray.exe [options]\r\n" +
               "\r\n" +
               "  -t <host>, --target=<host>   host to ping (default 1.1.1.1)\r\n" +
               "  -i <sec>,  --interval=<sec>  seconds between pings (default 5)\r\n" +
               "  -h, --help                   show this help and exit\r\n" +
               "  -v, --version                show version info and exit\r\n" +
               "\r\n" +
               "With no options, the settings last saved from the tray's\r\n" +
               "Settings dialog (settings.ini next to the exe) are used.\r\n" +
               "Command-line options always win and are never persisted.\r\n" +
               "One instance per target; different targets run side by side.";
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

    // Deliberately not a MessageBox: any MessageBox with an icon plays the
    // system notification sound, which is jarring for a plain About box.
    private static void ShowAboutDialog()
    {
        using (var form = new Form())
        {
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
                    Target = value;
                }
                else if (key == "interval")
                {
                    int seconds;
                    if (int.TryParse(value, out seconds) && seconds > 0)
                    {
                        IntervalMs = seconds * 1000;
                    }
                }
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
            File.WriteAllLines(ConfigPath, new[]
            {
                "target=" + Target,
                "interval=" + (IntervalMs / 1000)
            });
        }
        catch
        {
            // Best-effort; not critical if this fails (e.g. read-only folder).
        }
    }

    private static void ShowSettingsDialog()
    {
        using (var form = new Form())
        {
            form.Text = "Ping Tray Settings";
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.ClientSize = new Size(300, 150);

            var targetLabel = new Label { Text = "Target host:", Left = 15, Top = 20, Width = 90 };
            var targetBox = new TextBox { Text = Target, Left = 115, Top = 17, Width = 165 };

            var intervalLabel = new Label { Text = "Interval (sec):", Left = 15, Top = 55, Width = 90 };
            var intervalBox = new TextBox { Text = (IntervalMs / 1000).ToString(), Left = 115, Top = 52, Width = 165 };

            var okButton = new Button { Text = "OK", Left = 110, Top = 100, Width = 80, DialogResult = DialogResult.OK };
            var cancelButton = new Button { Text = "Cancel", Left = 200, Top = 100, Width = 80, DialogResult = DialogResult.Cancel };

            form.Controls.Add(targetLabel);
            form.Controls.Add(targetBox);
            form.Controls.Add(intervalLabel);
            form.Controls.Add(intervalBox);
            form.Controls.Add(okButton);
            form.Controls.Add(cancelButton);
            form.AcceptButton = okButton;
            form.CancelButton = cancelButton;

            if (form.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            string newTarget = targetBox.Text.Trim();
            int newIntervalSeconds;
            bool intervalValid = int.TryParse(intervalBox.Text.Trim(), out newIntervalSeconds) && newIntervalSeconds > 0;

            if (newTarget.Length == 0 || !intervalValid)
            {
                MessageBox.Show("Enter a valid host and a positive interval (seconds).", "Ping Tray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (newTarget != Target)
            {
                _consecutiveFailures = 0;
                _lastGoodMs = -1;
            }

            Target = newTarget;
            IntervalMs = newIntervalSeconds * 1000;
            _timer.Interval = IntervalMs;
            SaveSettings();
            UpdatePing();
        }
    }

    private static void SetTooltip(string text)
    {
        // NotifyIcon.Text throws if length >= 64, and the target host is
        // user-editable (Settings dialog / --target), so length isn't
        // predictable at the call sites. Truncate defensively instead of
        // trying to budget every format string by hand.
        if (text.Length > 63)
        {
            text = text.Substring(0, 60) + "...";
        }
        _notifyIcon.Text = text;
    }

    // Guards against overlapping attempts: a timed-out ping (up to 2s) can
    // outlive a short interval's next tick, and Ping throws if a send is
    // started while one is pending. Skipped ticks just wait for the next.
    private static bool _pingInFlight;

    // async void is fine here: it's a top-level event-driven handler, all
    // exceptions are caught inside, and the WinForms SynchronizationContext
    // resumes us on the UI thread, so the ping itself never blocks the
    // message pump (menu/tooltip stay responsive while a ping times out).
    private static async void UpdatePing()
    {
        const string RangeLegend = "(<50 / 50-150 / ≥150)";

        if (_pingInFlight)
        {
            return;
        }
        _pingInFlight = true;

        // Snapshot: Target can be retargeted from the Settings dialog while
        // this attempt is awaiting; label the result with the host actually
        // pinged.
        string target = Target;

        bool success = false;
        long ms = 0;
        string failureReason = null;

        try
        {
            PingReply reply = await _ping.SendPingAsync(target, PingTimeoutMs);
            if (reply.Status == IPStatus.Success)
            {
                success = true;
                ms = reply.RoundtripTime;
            }
            else
            {
                failureReason = reply.Status.ToString();
            }
        }
        catch (Exception ex)
        {
            failureReason = "error: " + ex.GetType().Name;
        }
        finally
        {
            _pingInFlight = false;
        }

        if (success)
        {
            _consecutiveFailures = 0;
            _lastGoodMs = ms;

            string text = ms >= 1000 ? "999+" : ms.ToString();
            Tone tone = ms < 50 ? Tone.Good : (ms < 150 ? Tone.Warn : Tone.Bad);

            SetTooltip(string.Format("Ping to {0}: {1} ms {2}", target, ms, RangeLegend));
            ApplyIcon(text, tone);
            return;
        }

        _consecutiveFailures++;

        if (_consecutiveFailures >= FailureThreshold)
        {
            SetTooltip(string.Format("Ping to {0}: offline ({1}) [{2}x]", target, failureReason, _consecutiveFailures));
            ApplyIcon("0", Tone.Offline);
        }
        else
        {
            // Below threshold: treat as a transient blip. Leave the icon
            // showing the last known-good reading; only the tooltip
            // reflects that attempts are currently failing.
            string lastGoodSuffix = _lastGoodMs >= 0 ? " (last: " + _lastGoodMs + "ms)" : "";
            SetTooltip(string.Format("Ping to {0}: no response {1}/{2}{3}", target, _consecutiveFailures, FailureThreshold, lastGoodSuffix));
        }
    }

    // What the number says. Each tone has a bright colour for a dark taskbar and a darker one
    // for a light taskbar (see ToneColor); Offline is black on both, with a light outline.
    private enum Tone { Good, Warn, Bad, Offline }

    private static Color ToneColor(Tone tone, bool light)
    {
        switch (tone)
        {
            case Tone.Good: return light ? Color.FromArgb(0, 130, 0) : Color.LimeGreen;
            case Tone.Warn: return light ? Color.FromArgb(205, 100, 0) : Color.Orange;
            case Tone.Bad: return light ? Color.FromArgb(200, 0, 0) : Color.Red;
            default: return Color.Black;
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
        // The black offline "0" always gets a light one so it shows on any taskbar.
        bool offline = tone == Tone.Offline;
        Color halo = (light || offline) ? Color.FromArgb(offline ? 235 : 190, 255, 255, 255) : Color.FromArgb(190, 15, 15, 15);
        using (var bmp = new Bitmap(S, S))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                DrawFit(g, text, color, halo, offline ? 3.6f : 2f, new RectangleF(1, 2, S - 2, S - 4));
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
    // with a thin halo so it reads on any taskbar.
    private static void DrawFit(Graphics g, string text, Color color, Color halo, float haloWidth, RectangleF box)
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
            using (var pen = new Pen(halo, haloWidth))
            using (var brush = new SolidBrush(color))
            {
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                g.DrawPath(pen, path);
                g.FillPath(brush, path);
            }
        }
    }
}
