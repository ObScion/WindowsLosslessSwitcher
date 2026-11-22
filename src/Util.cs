using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("LosslessSwitcher")]
[assembly: AssemblyProduct("LosslessSwitcher for Windows")]
[assembly: AssemblyDescription("Switches the output device's sample rate to match Apple Music.")]
[assembly: AssemblyCopyright("Copyright © 2022 Vincent Neo, © 2026 Jordan Gibson, © 2026 ObScion. GPL-3.0.")]
[assembly: AssemblyVersion("1.1.0.2")]
[assembly: AssemblyFileVersion("1.1.0.2")]
[assembly: AssemblyInformationalVersion("1.1")]

namespace LosslessSwitcher
{
    /// <summary>Port of Defaults.swift. Stored in %APPDATA%\LosslessSwitcher\settings.ini.</summary>
    sealed class Settings
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LosslessSwitcher");
        static readonly string FilePath = Path.Combine(Dir, "settings.ini");

        public bool ShowIcon = true;          // userPreferIconStatusBarItem
        public bool BitDepth = false;         // userPreferBitDepthDetection
        public bool PreferMultiples = false;  // userPreferSampleRateMultiples
        public bool AlbumRate = false;        // Windows addition: one rate per album (its highest track)
        public string DeviceId;               // selectedDeviceUID (null = Default Device)
        public string ScriptPath;             // shellScriptPath

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var kv = new Dictionary<string, string>();
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) kv[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                string v;
                if (kv.TryGetValue("ShowIcon", out v)) s.ShowIcon = v == "1";
                if (kv.TryGetValue("BitDepth", out v)) s.BitDepth = v == "1";
                if (kv.TryGetValue("PreferMultiples", out v)) s.PreferMultiples = v == "1";
                if (kv.TryGetValue("AlbumRate", out v)) s.AlbumRate = v == "1";
                if (kv.TryGetValue("DeviceId", out v) && v.Length > 0) s.DeviceId = v;
                if (kv.TryGetValue("ScriptPath", out v) && v.Length > 0) s.ScriptPath = v;
            }
            catch (Exception ex) { Log.Write("Settings load failed: " + ex.Message); }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, new[]
                {
                    "ShowIcon=" + (ShowIcon ? 1 : 0),
                    "BitDepth=" + (BitDepth ? 1 : 0),
                    "PreferMultiples=" + (PreferMultiples ? 1 : 0),
                    "AlbumRate=" + (AlbumRate ? 1 : 0),
                    "DeviceId=" + (DeviceId ?? ""),
                    "ScriptPath=" + (ScriptPath ?? ""),
                });
            }
            catch (Exception ex) { Log.Write("Settings save failed: " + ex.Message); }
        }
    }

    static class Log
    {
        static readonly string FilePath = Path.Combine(Settings.Dir, "log.txt");
        static readonly object Sync = new object();

        public static void Write(string message)
        {
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(Settings.Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
                catch { }
            }
        }
    }
}
