// Watches Apple Music's Windows media session (the equivalent of MediaRemoteAdapter on macOS).
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace LosslessSwitcher
{
    sealed class TrackWatcher
    {
        GlobalSystemMediaTransportControlsSessionManager _manager;
        GlobalSystemMediaTransportControlsSession _session;
        string _lastKey;

        /// <summary>Raised (on a worker thread) when Apple Music starts a different track.</summary>
        public event Action<TrackInfo> TrackChanged;

        static bool IsAppleMusic(string aumid)
        {
            return aumid != null &&
                (aumid.IndexOf("AppleInc.AppleMusicWin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 aumid.Equals("AppleMusic.exe", StringComparison.OrdinalIgnoreCase));
        }

        public async Task StartAsync()
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (s, e) => Attach();
            Attach();
        }

        void Attach()
        {
            try
            {
                var found = _manager.GetSessions().FirstOrDefault(x => IsAppleMusic(x.SourceAppUserModelId));
                if (found == _session) return;
                if (_session != null) _session.MediaPropertiesChanged -= OnPropertiesChanged;
                _session = found;
                if (_session == null) return;
                Log.Write("Attached to " + _session.SourceAppUserModelId);
                _session.MediaPropertiesChanged += OnPropertiesChanged;
                Read(_session);
            }
            catch (Exception ex) { Log.Write("Attach failed: " + ex.Message); }
        }

        void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) { Read(s); }

        async void Read(GlobalSystemMediaTransportControlsSession s)
        {
            try
            {
                var p = await s.TryGetMediaPropertiesAsync();
                // Apple Music briefly reports a "Connecting…" placeholder (no artist) between tracks.
                if (p == null || string.IsNullOrWhiteSpace(p.Title) || string.IsNullOrWhiteSpace(p.Artist)) return;
                var t = Normalize(p.Title, p.Artist, p.AlbumTitle);
                t.DetectedUtc = DateTime.UtcNow;
                lock (this)
                {
                    if (t.Key == _lastKey) return;
                    _lastKey = t.Key;
                }
                Log.Write("Track: " + t);
                var h = TrackChanged;
                if (h != null) h(t);
            }
            catch (Exception ex) { Log.Write("Reading media properties failed: " + ex.Message); }
        }

        // Apple Music for Windows reports "By <artist>" and may pack "<artist> — <album>" into the artist field.
        static TrackInfo Normalize(string title, string artist, string album)
        {
            title = (title ?? "").Trim();
            artist = (artist ?? "").Trim();
            album = (album ?? "").Trim();
            if (album.Length == 0)
            {
                foreach (var sep in new[] { " — ", " – " })
                {
                    int i = artist.IndexOf(sep, StringComparison.Ordinal);
                    if (i > 0 && i + sep.Length < artist.Length)
                    {
                        album = artist.Substring(i + sep.Length).Trim();
                        artist = artist.Substring(0, i).Trim();
                        break;
                    }
                }
            }
            if (artist.StartsWith("By ", StringComparison.Ordinal)) artist = artist.Substring(3).Trim();
            return new TrackInfo { Title = title, Artist = artist, Album = album };
        }

        public bool IsPlaying
        {
            get
            {
                try
                {
                    var s = _session;
                    return s != null && s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                }
                catch { return false; }
            }
        }

        /// <summary>Pause/play cycle that makes Apple Music rebuild a render stream that died in a format switch.</summary>
        public async Task NudgeAsync()
        {
            var s = _session;
            if (s == null) return;
            await s.TryPauseAsync();
            await Task.Delay(700);
            await s.TryPlayAsync();
        }
    }
}
