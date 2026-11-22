// Port of the macOS OutputDevices class: tracks devices, picks the nearest supported
// format for the playing track, applies it, and runs the user's script.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LosslessSwitcher
{
    sealed class OutputDevices
    {
        readonly Settings _settings;
        readonly TrackWatcher _watcher;
        readonly Detector _detector = new Detector();
        readonly object _switchLock = new object();
        readonly Dictionary<string, List<DevFormat>> _formatCache = new Dictionary<string, List<DevFormat>>();
        CancellationTokenSource _trackCts;
        int _generation;

        public List<DeviceInfo> Devices = new List<DeviceInfo>();
        public string DefaultDeviceId;
        public int? CurrentRate;   // Hz
        public int? CurrentBits;

        /// <summary>Raised from any thread when the displayed state changes.</summary>
        public event Action Changed;

        public OutputDevices(Settings settings, TrackWatcher watcher)
        {
            _settings = settings;
            _watcher = watcher;
            _watcher.TrackChanged += TrackDidChange;
            RefreshDevices();
            Task.Run(() => _detector.WarmUpAsync());
        }

        /// <summary>The user's pinned device, or the Windows default when unset/missing.</summary>
        public DeviceInfo SelectedDevice
        {
            get { return Devices.FirstOrDefault(d => d.Id == _settings.DeviceId); }
        }

        public DeviceInfo TargetDevice
        {
            get { return SelectedDevice ?? Devices.FirstOrDefault(d => d.Id == DefaultDeviceId); }
        }

        public void RefreshDevices()
        {
            try
            {
                Devices = CoreAudio.RenderDevices();
                DefaultDeviceId = CoreAudio.DefaultRenderId();
                lock (_switchLock) _formatCache.Clear();
            }
            catch (Exception ex) { Log.Write("Device enumeration failed: " + ex.Message); }
            ReadDeviceFormat();
        }

        public void SelectDevice(string id)
        {
            _settings.DeviceId = id;
            _settings.Save();
            ReadDeviceFormat();
        }

        /// <summary>getDeviceSampleRate(): show what the target device is currently set to.</summary>
        public void ReadDeviceFormat()
        {
            var dev = TargetDevice;
            var f = dev == null ? null : CoreAudio.GetDeviceFormat(dev.Id);
            if (f != null) UpdateSampleRate(f.Rate, f.Bits);
            else RaiseChanged();
        }

        void TrackDidChange(TrackInfo track)
        {
            CancellationTokenSource cts;
            int gen;
            lock (_switchLock)
            {
                if (_trackCts != null) _trackCts.Cancel();
                _trackCts = cts = new CancellationTokenSource();
                gen = ++_generation;
            }
            Task.Run(async () =>
            {
                try
                {
                    // Look up right away so the switch lands at the very start of the track (macOS waits
                    // 1 s for its logs to appear; the catalog doesn't need that). Retry every 2 s, since
                    // a local file may not be in Apple Music's cache yet.
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        var src = await _detector.DetectAsync(track, cts.Token);
                        if (src != null)
                        {
                            Log.Write("Detected " + src + " for " + track);
                            SwitchTo(src, gen, track.DetectedUtc);
                            return;
                        }
                        await Task.Delay(2000, cts.Token);
                    }
                    Log.Write("Could not determine the format of " + track + "; leaving the device as is.");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Write("Track processing failed: " + ex); }
            });
        }

        void SwitchTo(SourceFormat src, int gen, DateTime trackChangedUtc)
        {
            bool switched = false;
            string deviceId;
            lock (_switchLock)
            {
                if (gen != _generation) return;
                var dev = TargetDevice;
                if (dev == null) return;
                deviceId = dev.Id;
                var cur = CoreAudio.GetDeviceFormat(dev.Id);
                var target = PickFormat(dev.Id, cur, src);
                if (target == null) { Log.Write("No usable format for " + src); return; }

                if (cur != null && cur.Rate == target.Rate && cur.Bits == target.Bits)
                {
                    UpdateSampleRate(cur.Rate, cur.Bits);
                    return;
                }
                if (!CoreAudio.SetDeviceFormat(dev.Id, target))
                {
                    _formatCache.Remove(dev.Id);
                    return;
                }
                Log.Write("Switched " + dev.Name + " to " + target + ", " +
                    (int)(DateTime.UtcNow - trackChangedUtc).TotalMilliseconds + " ms after the track change");
                var now = CoreAudio.GetDeviceFormat(dev.Id) ?? target;
                UpdateSampleRate(now.Rate, now.Bits);
                switched = true;
            }
            if (switched && _watcher.IsPlaying) WatchAudioReturn(deviceId, gen);
        }

        DevFormat PickFormat(string deviceId, DevFormat cur, SourceFormat src)
        {
            List<DevFormat> supported;
            if (!_formatCache.TryGetValue(deviceId, out supported))
            {
                supported = CoreAudio.ProbeFormats(deviceId, cur);
                Log.Write("Device supports: " + (supported.Count == 0 ? "(exclusive probe unavailable)" : string.Join(", ", supported.Select(f => f.Bits + "/" + f.Rate))));
                _formatCache[deviceId] = supported;
            }
            if (supported.Count == 0)
            {
                // Exclusive mode disabled or device busy: fall back to "try it" with the current shape.
                if (cur == null) return null;
                supported = CoreAudio.StandardRates.Select(r => new DevFormat { Rate = r, Bits = cur.Bits, Container = cur.Container, Channels = cur.Channels, Mask = cur.Mask }).ToList();
            }

            var rates = supported.Select(f => f.Rate).Distinct().ToList();
            int nearest = rates.OrderBy(r => Math.Abs((long)r - src.Rate)).First();

            if (_settings.PreferMultiples && nearest != src.Rate)
            {
                int baseRate = src.Rate % 44100 == 0 ? 44100 : 48000;
                var family = rates.Where(r => r % baseRate == 0 && r < src.Rate).ToList();
                if (family.Count > 0) nearest = family.Max();
            }

            var atRate = supported.Where(f => f.Rate == nearest).ToList();
            int wantBits = _settings.BitDepth || cur == null ? src.Bits : cur.Bits;
            int bits = atRate.Select(f => f.Bits).OrderBy(b => Math.Abs(b - wantBits)).First();
            return atRate.Where(f => f.Bits == bits)
                .OrderBy(f => cur != null && f.Container == cur.Container ? 0 : 1)
                .First();
        }

        // A live format change invalidates Apple Music's render stream; it normally rebuilds it within
        // a few seconds. Logs how long the gap lasted; if the device is still silent after 8 s while
        // Apple Music claims to be playing, nudge it once.
        void WatchAudioReturn(string deviceId, int gen)
        {
            Task.Run(async () =>
            {
                const float Silence = 0.0005f;
                var sw = Stopwatch.StartNew();
                // Phase 1: the old stream keeps playing until Windows tears it down; wait for the silence to start.
                long gapStart = -1;
                while (sw.ElapsedMilliseconds < 3000)
                {
                    if (gen != _generation) return;
                    var peak = CoreAudio.Peak(deviceId);
                    if (peak < 0) return; // no meter on this device
                    if (peak <= Silence) { gapStart = sw.ElapsedMilliseconds; break; }
                    await Task.Delay(20);
                }
                if (gapStart < 0) { Log.Write("No dropout detected in the 3 s after the switch."); return; }
                // Phase 2: wait for Apple Music's rebuilt stream to produce sound.
                while (sw.ElapsedMilliseconds < gapStart + 8000)
                {
                    if (gen != _generation || !_watcher.IsPlaying) return;
                    var peak = CoreAudio.Peak(deviceId);
                    if (peak > Silence)
                    {
                        Log.Write("Dropout started " + gapStart + " ms after the switch and lasted " +
                            (sw.ElapsedMilliseconds - gapStart) + " ms.");
                        return;
                    }
                    await Task.Delay(20);
                }
                if (gen != _generation || !_watcher.IsPlaying) return;
                Log.Write("No audio 8 s after the switch; nudging Apple Music (pause/play).");
                try { await _watcher.NudgeAsync(); } catch (Exception ex) { Log.Write("Nudge failed: " + ex.Message); }
            });
        }

        void UpdateSampleRate(int rate, int? bits)
        {
            bool changed = CurrentRate != rate || CurrentBits != bits;
            CurrentRate = rate;
            CurrentBits = bits;
            RaiseChanged();
            if (changed) RunUserScript(rate, bits);
        }

        void RaiseChanged()
        {
            var h = Changed;
            if (h != null) h();
        }

        /// <summary>Invokes the selected script with arguments: sampleRate [bitDepth].</summary>
        void RunUserScript(int rate, int? bits)
        {
            var path = _settings.ScriptPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var args = rate + (bits.HasValue ? " " + bits.Value : "");
            try
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var psi = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                if (ext == ".ps1") { psi.FileName = "powershell.exe"; psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + path + "\" " + args; }
                else if (ext == ".bat" || ext == ".cmd") { psi.FileName = "cmd.exe"; psi.Arguments = "/c \"\"" + path + "\" " + args + "\""; }
                else { psi.FileName = path; psi.Arguments = args; }
                Process.Start(psi);
            }
            catch (Exception ex) { Log.Write("Script failed: " + ex.Message); }
        }
    }
}
