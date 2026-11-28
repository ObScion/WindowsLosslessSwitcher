// Tray app: the Windows equivalent of the macOS MenuBarExtra + MenuView.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LosslessSwitcher
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (new Mutex(true, @"Local\LosslessSwitcher-Windows", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Theme.ApplyMenuTheme();
                Application.Run(new TrayApp());
            }
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "LosslessSwitcher";

        readonly SynchronizationContext _ui;
        readonly Settings _settings = Settings.Load();
        readonly TrackWatcher _watcher = new TrackWatcher();
        readonly OutputDevices _devices;
        readonly NotifyIcon _tray;
        readonly ContextMenu _menu = new ContextMenu();
        readonly DeviceNotifier _notifier;
        Icon _currentIcon;

        public TrayApp()
        {
            _ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_ui);

            _devices = new OutputDevices(_settings, _watcher);
            _devices.Changed += () => _ui.Post(_ => UpdateTray(), null);

            _menu.Popup += (s, e) => BuildMenu();
            _tray = new NotifyIcon { ContextMenu = _menu, Visible = true };
            _tray.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var show = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                if (show != null) show.Invoke(_tray, null);
            };
            UpdateTray();

            _notifier = new DeviceNotifier(defaultChanged => _ui.Post(_ => _devices.RefreshDevices(), null));
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                Theme.ApplyMenuTheme();
                UpdateTray();
            };

            StartWatcher();
        }

        async void StartWatcher()
        {
            try { await _watcher.StartAsync(); }
            catch (Exception ex) { Log.Write("Media session watcher failed to start: " + ex.Message); }
        }

        // ---------------------------------------------------------------- display

        /// <summary>"%.1f kHz" or "%.1f kHz / %d bit", exactly like the macOS app.</summary>
        string SampleRateText
        {
            get
            {
                if (!_devices.CurrentRate.HasValue) return null;
                var khz = string.Format(CultureInfo.InvariantCulture, "{0:0.0} kHz", _devices.CurrentRate.Value / 1000.0);
                if (!_settings.BitDepth) return khz;
                return khz + " / " + (_devices.CurrentBits.HasValue ? _devices.CurrentBits.Value.ToString() : "?") + " bit";
            }
        }

        void UpdateTray()
        {
            string label = null;
            if (!_settings.ShowIcon)
            {
                label = _devices.CurrentRate.HasValue
                    ? (_devices.CurrentRate.Value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)
                    : "?";
            }
            var icon = Theme.RenderTrayIcon(label);
            _tray.Icon = icon;
            if (_currentIcon != null) _currentIcon.Dispose();
            _currentIcon = icon;

            var dev = _devices.TargetDevice;
            var tip = (SampleRateText ?? "Unknown") + (dev != null ? "\n" + dev.Name : "");
            _tray.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;
        }

        // ---------------------------------------------------------------- menu

        void BuildMenu()
        {
            _menu.MenuItems.Clear();
            var items = _menu.MenuItems;

            // ContentView: sample rate (bold) and device name (secondary).
            var rate = SampleRateText;
            if (rate != null) items.Add(new MenuItem(rate) { DefaultItem = true });
            var dev = _devices.TargetDevice;
            if (dev != null) items.Add(new MenuItem(dev.Name) { Enabled = false });
            if (items.Count > 0) items.Add("-");

            items.Add(new MenuItem(_settings.ShowIcon ? "Show Sample Rate" : "Show Icon", (s, e) =>
            {
                _settings.ShowIcon = !_settings.ShowIcon;
                _settings.Save();
                UpdateTray();
            }));

            items.Add(new MenuItem("Bit Depth Switching", (s, e) =>
            {
                _settings.BitDepth = !_settings.BitDepth;
                _settings.Save();
                UpdateTray();
            }) { Checked = _settings.BitDepth });

            items.Add(new MenuItem("Prefer Closest Sample Rate Multiple", (s, e) =>
            {
                _settings.PreferMultiples = !_settings.PreferMultiples;
                _settings.Save();
            }) { Checked = _settings.PreferMultiples });

            items.Add(new MenuItem("Match Album Rate", (s, e) =>
            {
                _settings.AlbumRate = !_settings.AlbumRate;
                _settings.Save();
            }) { Checked = _settings.AlbumRate });

            var selected = _devices.SelectedDevice;
            var devicesMenu = new MenuItem("Selected Device");
            devicesMenu.MenuItems.Add(new MenuItem("Default Device", (s, e) => _devices.SelectDevice(null))
                { Checked = selected == null, RadioCheck = true });
            foreach (var d in _devices.Devices)
            {
                var id = d.Id;
                devicesMenu.MenuItems.Add(new MenuItem(d.Name.Replace("&", "&&"), (s, e) => _devices.SelectDevice(id))
                    { Checked = selected != null && selected.Id == id, RadioCheck = true });
            }
            items.Add(devicesMenu);

            items.Add(new MenuItem("Launch at Login", (s, e) => SetLaunchAtLogin(!LaunchAtLogin)) { Checked = LaunchAtLogin });

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var about = new MenuItem("About");
            about.MenuItems.Add(new MenuItem("Version - " + version.Major + "." + version.Minor) { Enabled = false });
            about.MenuItems.Add(new MenuItem("Build - " + version.Revision) { Enabled = false });
            about.MenuItems.Add("-");
            // GPL-3.0 "Appropriate Legal Notices": copyright, no warranty, and how to view the license.
            about.MenuItems.Add(new MenuItem("© 2022 Vincent Neo (LosslessSwitcher)") { Enabled = false });
            about.MenuItems.Add(new MenuItem("© 2026 Jordan Gibson (WindowsLosslessSwitcher)") { Enabled = false });
            about.MenuItems.Add(new MenuItem("© 2026 ObScion (this Windows port)") { Enabled = false });
            about.MenuItems.Add(new MenuItem("Free software under GPL-3.0, with no warranty") { Enabled = false });
            about.MenuItems.Add(new MenuItem("Tray font: IBM Plex Sans Condensed, © 2017 IBM Corp. (SIL OFL 1.1)") { Enabled = false });
            about.MenuItems.Add(new MenuItem("View License", (s, e) => OpenUrl(RepoUrl + "/blob/lite/LICENSE")));
            about.MenuItems.Add(new MenuItem("Source Code", (s, e) => OpenUrl(RepoUrl + "/tree/lite")));
            about.MenuItems.Add("-");
            about.MenuItems.Add(new MenuItem("Open Log", (s, e) => OpenLog()));
            items.Add(about);

            var scripting = new MenuItem("Scripting");
            scripting.MenuItems.Add(new MenuItem("Select Script...", (s, e) => SelectScript()));
            scripting.MenuItems.Add(new MenuItem("Clear Selection", (s, e) => { _settings.ScriptPath = null; _settings.Save(); }));
            scripting.MenuItems.Add(new MenuItem((_settings.ScriptPath ?? "No selection").Replace("&", "&&")) { Enabled = false });
            items.Add(scripting);

            items.Add(new MenuItem("Quit LosslessSwitcher", (s, e) => Quit()));
        }

        void SelectScript()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Select a script that should be invoked when sample rate changes.",
                Filter = "Scripts and programs (*.bat;*.cmd;*.ps1;*.exe)|*.bat;*.cmd;*.ps1;*.exe|All files (*.*)|*.*",
            })
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    _settings.ScriptPath = dlg.FileName;
                    _settings.Save();
                }
            }
        }

        const string RepoUrl = "https://github.com/ObScion/WindowsLosslessSwitcher";

        static void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("Could not open " + url + ": " + ex.Message); }
        }

        static void OpenLog()
        {
            var path = System.IO.Path.Combine(Settings.Dir, "log.txt");
            if (System.IO.File.Exists(path)) System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\"");
        }

        static bool LaunchAtLogin
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(AppName) != null;
            }
        }

        static void SetLaunchAtLogin(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(AppName, false);
            }
        }

        void Quit()
        {
            _notifier.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            ExitThread();
        }
    }

    static class Theme
    {
        [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        // Checking one font by name, rather than loading the whole system font list.
        static readonly string GlyphFont = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        static bool FontExists(string name)
        {
            using (var f = new Font(name, 10f)) return f.Name == name; // Windows substitutes a fallback when missing
        }

        static int ReadPersonalize(string name, int fallback)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue(name);
                    return v is int ? (int)v : fallback;
                }
            }
            catch { return fallback; }
        }

        /// <summary>Native menus follow the Windows app theme (dark/light).</summary>
        public static void ApplyMenuTheme()
        {
            try
            {
                bool dark = ReadPersonalize("AppsUseLightTheme", 1) == 0;
                SetPreferredAppMode(dark ? 2 /* ForceDark */ : 3 /* ForceLight */);
                FlushMenuThemes();
            }
            catch { }
        }

        /// <summary>Music note glyph (label == null) or the sample rate in kHz, drawn for the taskbar's theme.</summary>
        public static Icon RenderTrayIcon(string label)
        {
            return RenderTrayIcon(label, SystemInformation.SmallIconSize.Width);
        }

        // The tray label font: IBM Plex Sans Condensed SemiBold (SIL Open Font License 1.1), embedded in the exe
        // gzip-compressed and loaded into memory once. Falls back to Bahnschrift if it can't be loaded.
        static readonly PrivateFontCollection LabelFonts = new PrivateFontCollection();
        static readonly FontFamily LabelFamily = LoadLabelFont();

        static FontFamily LoadLabelFont()
        {
            try
            {
                using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("LabelFont.ttf.gz"))
                using (var gz = new System.IO.Compression.GZipStream(res, System.IO.Compression.CompressionMode.Decompress))
                using (var ms = new System.IO.MemoryStream())
                {
                    gz.CopyTo(ms);
                    var bytes = ms.ToArray();
                    // GDI+ reads from this memory whenever the font is used, so it is deliberately never freed.
                    var mem = Marshal.AllocCoTaskMem(bytes.Length);
                    Marshal.Copy(bytes, 0, mem, bytes.Length);
                    LabelFonts.AddMemoryFont(mem, bytes.Length);
                    return LabelFonts.Families[0];
                }
            }
            catch (Exception ex)
            {
                Log.Write("Tray label font failed to load (" + ex.Message + "); using Bahnschrift.");
                return new FontFamily(FontExists("Bahnschrift") ? "Bahnschrift" : "Segoe UI");
            }
        }

        static GraphicsPath Outline(string s)
        {
            var p = new GraphicsPath();
            var style = LabelFamily.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
            p.AddString(s, LabelFamily, (int)style, 100f, PointF.Empty, StringFormat.GenericTypographic);
            return p;
        }

        static void Place(Graphics g, Brush brush, GraphicsPath p, RectangleF b, float x, float y, float sx, float sy)
        {
            using (var m = new Matrix())
            {
                m.Translate(x, y);
                m.Scale(sx, sy);
                m.Translate(-b.X, -b.Y);
                p.Transform(m);
            }
            g.FillPath(brush, p);
        }

        /// <summary>
        /// Draws the rate as glyph outlines scaled to fill the icon (drawn as plain text, digits end up a third
        /// of the icon tall, lost in font padding). Whole numbers use the full height, narrowed up to 35% for
        /// three digits. With a decimal, one line can't stay legible in 16 px, so the integer goes on top
        /// (about 58% of the height) and the decimal, smaller, sits at the bottom right after a whole-pixel dot.
        /// </summary>
        static void DrawLabel(Graphics g, Brush brush, string label, int size)
        {
            var parts = label.Split('.');
            int t = Math.Max(1, (int)Math.Round(size / 16.0)); // dot size and gaps
            int margin = size >= 24 ? 2 : 1;
            int full = size - 2 * margin;
            using (var ip = Outline(parts[0]))
            using (var dp = parts.Length > 1 ? Outline(parts[1]) : null)
            {
                var ib = ip.GetBounds();
                if (ib.Width <= 0 || ib.Height <= 0) return;
                if (dp == null)
                {
                    int h = full;
                    float sy = h / ib.Height, sx = sy, availW = size - margin;
                    if (ib.Width * sx > availW)
                    {
                        sx = availW / ib.Width;
                        if (sx < sy * 0.65f) { h = (int)Math.Floor(ib.Height * sx / 0.65f); sy = h / ib.Height; }
                    }
                    Place(g, brush, ip, ib, (size - ib.Width * sx) / 2f, margin + (full - h) / 2, sx, sy);
                    return;
                }
                var db = dp.GetBounds();
                int ht = (int)Math.Round(size * 0.58), hd = size - ht - t;
                float st = ht / ib.Height, stx = Math.Min(st, size / ib.Width);
                float wt = ib.Width * stx, xt = (size - wt) / 2f;
                Place(g, brush, ip, ib, xt, 0, stx, st);
                float sd = hd / db.Height, xd = xt + wt - db.Width * sd;
                Place(g, brush, dp, db, xd, size - hd, sd, sd);
                g.FillRectangle(brush, Math.Max(0, (int)Math.Round(xd) - 2 * t), size - t, t, t);
            }
        }

        public static Icon RenderTrayIcon(string label, int size)
        {
            var color = ReadPersonalize("SystemUsesLightTheme", 0) == 1 ? Color.Black : Color.White;
            using (var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(color))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var rect = new RectangleF(0, 0, size, size);
                if (label == null)
                {
                    using (var font = new Font(GlyphFont, size * 0.8f, GraphicsUnit.Pixel))
                        g.DrawString("", font, brush, rect, fmt);
                }
                else
                {
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    DrawLabel(g, brush, label, size);
                }
                var h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
        }
    }
}
