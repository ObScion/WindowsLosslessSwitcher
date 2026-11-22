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
            int size = SystemInformation.SmallIconSize.Width;
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
                    float px = size * 0.72f;
                    Font font = null;
                    try
                    {
                        while (true)
                        {
                            font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
                            if (px <= 5 || g.MeasureString(label, font, PointF.Empty, StringFormat.GenericTypographic).Width <= size) break;
                            font.Dispose();
                            px -= 0.5f;
                        }
                        rect.Inflate(2, 0);
                        g.DrawString(label, font, brush, rect, fmt);
                    }
                    finally { if (font != null) font.Dispose(); }
                }
                var h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
        }
    }
}
