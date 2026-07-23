using System;
using System.Drawing;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class PingTray
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    private static string Target = "1.1.1.1";
    private static int IntervalMs = 3000;

    // A single dropped ICMP reply is common on flaky links (e.g. 4G) and
    // isn't necessarily "down" — only flip to offline after this many
    // consecutive failed attempts. Each attempt waits up to PingTimeoutMs
    // before being counted as failed.
    private const int FailureThreshold = 3;
    private const int PingTimeoutMs = 2000;
    private static int _consecutiveFailures = 0;
    private static long _lastGoodMs = -1;

    // Identifies which saved-settings file this instance reads/writes.
    // Fixed at the target this instance was launched with, so retargeting
    // via the Settings dialog doesn't bleed into another instance's file
    // (each shortcut/launch config keeps its own persisted slot).
    private static string ProfileKey;
    private static string ConfigPath;

    private static NotifyIcon _notifyIcon;
    private static Ping _ping;
    private static Icon _currentIcon;
    private static Timer _timer;

    [STAThread]
    private static void Main(string[] args)
    {
        ParseArgs(args);

        ProfileKey = Target;
        ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings-" + SanitizeForFilename(ProfileKey) + ".ini");
        LoadSettings();

        // Prevent duplicate instances monitoring the same resolved target
        // (different targets are allowed to run side by side).
        bool createdNew;
        var mutex = new System.Threading.Mutex(true, "Global\\PingTray_SingleInstance_" + Target, out createdNew);
        if (!createdNew)
        {
            return;
        }

        FreeConsole();

        Application.EnableVisualStyles();
        _ping = new Ping();

        var menu = new ContextMenuStrip();
        var settingsItem = menu.Items.Add("Settings...");
        settingsItem.Click += (s, e) => ShowSettingsDialog();
        menu.Items.Add(new ToolStripSeparator());
        var exitItem = menu.Items.Add("Exit");
        exitItem.Click += (s, e) => Application.Exit();

        _notifyIcon = new NotifyIcon();
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

    private static void ParseArgs(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg.StartsWith("--target=", StringComparison.OrdinalIgnoreCase))
            {
                string value = arg.Substring("--target=".Length).Trim();
                if (value.Length > 0)
                {
                    Target = value;
                }
            }
            else if (arg.StartsWith("--interval=", StringComparison.OrdinalIgnoreCase))
            {
                int seconds;
                if (int.TryParse(arg.Substring("--interval=".Length), out seconds) && seconds > 0)
                {
                    IntervalMs = seconds * 1000;
                }
            }
        }
    }

    private static string SanitizeForFilename(string value)
    {
        var sb = new StringBuilder();
        foreach (char c in value)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_');
        }
        return sb.ToString();
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

    private static void UpdatePing()
    {
        const string RangeLegend = "(<50 / 50-150 / ≥150)";

        bool success = false;
        long ms = 0;
        string failureReason = null;

        try
        {
            PingReply reply = _ping.Send(Target, PingTimeoutMs);
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

        if (success)
        {
            _consecutiveFailures = 0;
            _lastGoodMs = ms;

            string text;
            float fontSize;
            Color color;

            if (ms >= 1000) { text = "999+"; fontSize = 24f; }
            else if (ms >= 100) { text = ms.ToString(); fontSize = 34f; }
            else if (ms >= 10) { text = ms.ToString(); fontSize = 46f; }
            else { text = ms.ToString(); fontSize = 56f; }

            if (ms < 50) color = Color.LimeGreen;
            else if (ms < 150) color = Color.Orange;
            else color = Color.Red;

            SetTooltip(string.Format("Ping to {0}: {1} ms {2}", Target, ms, RangeLegend));
            ApplyIcon(text, color, fontSize);
            return;
        }

        _consecutiveFailures++;

        if (_consecutiveFailures >= FailureThreshold)
        {
            SetTooltip(string.Format("Ping to {0}: offline ({1}) [{2}x]", Target, failureReason, _consecutiveFailures));
            ApplyIcon("0", Color.Black, 56f);
        }
        else
        {
            // Below threshold: treat as a transient blip. Leave the icon
            // showing the last known-good reading; only the tooltip
            // reflects that attempts are currently failing.
            string lastGoodSuffix = _lastGoodMs >= 0 ? " (last: " + _lastGoodMs + "ms)" : "";
            SetTooltip(string.Format("Ping to {0}: no response {1}/{2}{3}", Target, _consecutiveFailures, FailureThreshold, lastGoodSuffix));
        }
    }

    private static void ApplyIcon(string text, Color color, float fontSize)
    {
        Icon newIcon = RenderIcon(text, color, fontSize);
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

    private static Icon RenderIcon(string text, Color color, float fontSize)
    {
        using (var bmp = new Bitmap(64, 64))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(color))
                {
                    var sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(text, font, brush, 32, 32, sf);
                }
            }

            IntPtr hIcon = bmp.GetHicon();
            return Icon.FromHandle(hIcon);
        }
    }
}
