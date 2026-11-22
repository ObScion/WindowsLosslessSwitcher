// Works out the sample rate / bit depth of the track Apple Music is playing.
// macOS reads this from Apple Music's decoder logs; Windows has no such log, so this uses:
//   1. Apple Music's PlayCache (local / cloud-library files): parse the actual file header.
//   2. Apple's public web catalog: the track's lossless HLS manifest lists its master format.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;

namespace LosslessSwitcher
{
    sealed class TrackInfo
    {
        public string Title, Artist, Album;
        public DateTime DetectedUtc;
        public string Key { get { return Title + "\u001f" + Artist + "\u001f" + Album; } }
        public override string ToString() { return "'" + Title + "' by '" + Artist + "' (" + Album + ")"; }
    }

    sealed class SourceFormat
    {
        public int Rate, Bits;
        public string Origin;
        public override string ToString() { return Bits + "/" + Rate + " via " + Origin; }
    }

    sealed class Detector
    {
        static readonly HttpClient Http = CreateHttp();
        static readonly string PlayCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Packages\AppleInc.AppleMusicWin_nzyj5cx40ttqa\LocalCache\Local\Apple\AMPLibraryAgent\PlayCache");
        static readonly string[] MediaExtensions = { ".mp3", ".m4a", ".mp4", ".wav", ".aif", ".aiff" };

        readonly string _storefront;
        readonly Dictionary<string, SourceFormat> _catalogMemo = new Dictionary<string, SourceFormat>(); // null value = no match
        string _token;
        DateTime _tokenExpiresUtc;

        // Catalog results survive restarts, so repeat plays switch without a network round trip.
        // One line per track: storefront, title, artist, album, rate, bits (tab-separated).
        static readonly string CacheFile = Path.Combine(Settings.Dir, "catalog-cache.tsv");

        public Detector()
        {
            string region = "us";
            try { region = RegionInfo.CurrentRegion.TwoLetterISORegionName.ToLowerInvariant(); } catch { }
            _storefront = region.Length == 2 ? region : "us";
            LoadCache();
        }

        /// <summary>Fetch the catalog token up front so the first track change isn't slowed by it.</summary>
        public async Task WarmUpAsync()
        {
            try { await TokenAsync(CancellationToken.None); }
            catch (Exception ex) { Log.Write("Catalog warm-up failed: " + ex.Message); }
        }

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }

        void LoadCache()
        {
            try
            {
                if (!File.Exists(CacheFile)) return;
                int n = 0;
                foreach (var line in File.ReadAllLines(CacheFile))
                {
                    var p = line.Split('\t');
                    int rate, bits;
                    if (p.Length != 6 || p[0] != _storefront || !int.TryParse(p[4], out rate) || !int.TryParse(p[5], out bits)) continue;
                    var key = new TrackInfo { Title = p[1], Artist = p[2], Album = p[3] }.Key;
                    _catalogMemo[key] = new SourceFormat { Rate = rate, Bits = bits, Origin = "saved catalog result" };
                    n++;
                }
                Log.Write("Loaded " + n + " saved catalog results.");
            }
            catch (Exception ex) { Log.Write("Catalog cache load failed: " + ex.Message); }
        }

        void SaveToCache(TrackInfo t, SourceFormat f)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                lock (_catalogMemo)
                    File.AppendAllText(CacheFile, string.Join("\t", _storefront, Clean(t.Title), Clean(t.Artist), Clean(t.Album), f.Rate, f.Bits) + Environment.NewLine);
            }
            catch (Exception ex) { Log.Write("Catalog cache save failed: " + ex.Message); }
        }

        static HttpClient CreateHttp()
        {
            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12 | (System.Net.SecurityProtocolType)12288 /* Tls13 */;
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "LosslessSwitcher-Windows/1.0");
            return c;
        }

        public async Task<SourceFormat> DetectAsync(TrackInfo t, CancellationToken ct)
        {
            // An in-progress local download named after this track is the strongest evidence.
            var f = Safe(() => MatchDownloadFolder(t));
            if (f != null) return f;
            f = await CatalogAsync(t, ct);
            if (f != null) return f;
            return Safe(() => MatchFreshFile(t)) ?? Safe(() => MatchCacheInfo(t));
        }

        static SourceFormat Safe(Func<SourceFormat> fn)
        {
            try { return fn(); }
            catch (Exception ex) { Log.Write("PlayCache lookup failed: " + ex.Message); return null; }
        }

        // ---------------------------------------------------------------- PlayCache

        static IEnumerable<FileInfo> MediaFiles(string pattern)
        {
            if (!Directory.Exists(PlayCacheDir)) return Enumerable.Empty<FileInfo>();
            return Directory.EnumerateFiles(PlayCacheDir, pattern, SearchOption.AllDirectories)
                .Where(p => MediaExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc);
        }

        // In-progress downloads: PlayCache\Downloads\<Title> _ <Album>.tmp\download.m4a (name truncated ~31 chars).
        static SourceFormat MatchDownloadFolder(TrackInfo t)
        {
            foreach (var fi in MediaFiles("download.*"))
            {
                var dir = fi.Directory.Name;
                if (!dir.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                if (!FolderMatches(t, dir.Substring(0, dir.Length - 4))) continue;
                var f = AudioFileProbe.Probe(fi.FullName);
                if (f != null) { f.Origin = "download " + dir; return f; }
            }
            return null;
        }

        static bool FolderMatches(TrackInfo t, string folder)
        {
            var title = SanitizeFileName(t.Title.Trim());
            if (folder.Equals(title, StringComparison.OrdinalIgnoreCase) ||
                folder.StartsWith(title + " _ ", StringComparison.OrdinalIgnoreCase)) return true;
            if (folder.Length < 30) return false;
            var combined = string.IsNullOrEmpty(t.Album) ? title : title + " _ " + SanitizeFileName(t.Album.Trim());
            return combined.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        }

        static string SanitizeFileName(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            return new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray());
        }

        // A cache file written right around the track change is that track's download completing.
        static SourceFormat MatchFreshFile(TrackInfo t)
        {
            var cutoff = t.DetectedUtc - TimeSpan.FromSeconds(15);
            foreach (var fi in MediaFiles("*").Where(x => x.LastWriteTimeUtc >= cutoff))
            {
                var f = AudioFileProbe.Probe(fi.FullName);
                if (f != null) { f.Origin = "fresh cache file " + fi.Name; return f; }
            }
            return null;
        }

        // Completed downloads are named <libraryId>-<cloudId hex>; PlayCacheInfo.xml records access dates.
        static SourceFormat MatchCacheInfo(TrackInfo t)
        {
            var info = Path.Combine(PlayCacheDir, "PlayCacheInfo.xml");
            if (!File.Exists(info)) return null;
            XDocument doc;
            using (var s = new FileStream(info, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = System.Xml.XmlReader.Create(s, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null }))
                doc = XDocument.Load(r);

            DateTime newest = DateTime.MinValue;
            long cloudId = 0;
            foreach (var dict in doc.Descendants("array").SelectMany(a => a.Elements("dict")))
            {
                var kids = dict.Elements().ToList();
                DateTime? date = null; long id = 0;
                for (int i = 0; i + 1 < kids.Count; i += 2)
                {
                    DateTime d; long n;
                    if (kids[i].Value == "access-date" && DateTime.TryParse(kids[i + 1].Value, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d)) date = d;
                    else if (kids[i].Value == "cloud-id" && long.TryParse(kids[i + 1].Value, out n)) id = n;
                }
                if (date.HasValue && id > 0 && date.Value > newest) { newest = date.Value; cloudId = id; }
            }
            if (cloudId == 0 || newest < t.DetectedUtc - TimeSpan.FromMinutes(2)) return null;
            var suffix = "-" + cloudId.ToString("X16");
            var file = MediaFiles("*").FirstOrDefault(x => Path.GetFileNameWithoutExtension(x.Name).EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (file == null) return null;
            var f = AudioFileProbe.Probe(file.FullName);
            if (f != null) f.Origin = "cache info " + file.Name;
            return f;
        }

        // ---------------------------------------------------------------- Catalog

        async Task<SourceFormat> CatalogAsync(TrackInfo t, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(t.Title) || string.IsNullOrWhiteSpace(t.Artist)) return null;
            SourceFormat memo;
            lock (_catalogMemo) if (_catalogMemo.TryGetValue(t.Key, out memo)) return memo;
            try
            {
                var token = await TokenAsync(ct);
                if (token == null) return null;
                var match = await SearchAsync(t, token, ct);
                SourceFormat result = null;
                if (match != null)
                {
                    var hls = await SongManifestUrlAsync(match, token, ct);
                    if (hls == null) return null; // transient, don't memo
                    if (hls.Length > 0)
                    {
                        var manifest = await GetAsync(hls, null, ct);
                        if (manifest == null) return null;
                        result = BestLosslessVariant(manifest);
                        if (result != null) result.Origin = "catalog song " + match;
                        Log.Write("Catalog song " + match + " variants: " + string.Join(", ",
                            Regex.Matches(manifest, @"#EXT-X-MEDIA:TYPE=AUDIO[^\n]*").Cast<Match>()
                                .Select(m => Regex.Match(m.Value, @"GROUP-ID=""([^""]+)""").Groups[1].Value)
                                .Where(g => g.Length > 0).Distinct()));
                    }
                }
                lock (_catalogMemo) _catalogMemo[t.Key] = result;
                if (result == null) Log.Write("Catalog: no lossless match for " + t);
                else SaveToCache(t, result);  // only positive matches persist; a miss is retried next session
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Write("Catalog lookup failed: " + ex.Message); return null; }
        }

        async Task<string> GetAsync(string url, string token, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (token != null)
                {
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    req.Headers.TryAddWithoutValidation("Origin", "https://music.apple.com");
                }
                using (var resp = await Http.SendAsync(req, ct))
                    return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync() : null;
            }
        }

        // The public web player embeds a developer token in its JS bundle.
        async Task<string> TokenAsync(CancellationToken ct)
        {
            if (_token != null && _tokenExpiresUtc > DateTime.UtcNow.AddMinutes(5)) return _token;
            var html = await GetAsync("https://music.apple.com/" + _storefront + "/browse", null, ct);
            var m = html == null ? Match.Empty : Regex.Match(html, "src=\"(?<p>/assets/index[^\"']+\\.js)\"");
            if (!m.Success) { Log.Write("Catalog: web player bundle not found"); return null; }
            var js = await GetAsync("https://music.apple.com" + m.Groups["p"].Value, null, ct);
            var tm = js == null ? Match.Empty : Regex.Match(js, @"eyJ[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+");
            if (!tm.Success) { Log.Write("Catalog: token not found"); return null; }
            _token = tm.Value;
            _tokenExpiresUtc = DateTime.UtcNow.AddHours(6);
            try
            {
                var seg = _token.Split('.')[1].Replace('-', '+').Replace('_', '/');
                seg = seg.PadRight(seg.Length + (4 - seg.Length % 4) % 4, '=');
                var payload = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Convert.FromBase64String(seg)));
                object exp;
                if (payload.TryGetValue("exp", out exp)) _tokenExpiresUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Convert.ToDouble(exp));
            }
            catch { }
            return _token;
        }

        async Task<string> SearchAsync(TrackInfo t, string token, CancellationToken ct)
        {
            var queries = new List<string>();
            Action<string> add = q => { q = q.Trim(); if (q.Length > 0 && !queries.Contains(q, StringComparer.OrdinalIgnoreCase)) queries.Add(q); };
            add(t.Title + " " + PrimaryArtist(t.Artist));
            add(t.Title + " " + t.Artist);
            add(t.Title + " " + t.Album);
            add(t.Title);

            foreach (var q in queries)
            {
                var url = "https://amp-api.music.apple.com/v1/catalog/" + _storefront + "/search?types=songs&limit=25&term=" + Uri.EscapeDataString(q);
                var json = await GetAsync(url, token, ct);
                if (json == null) continue;
                var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                var data = Dig(root, "results", "songs", "data") as object[];
                if (data == null) continue;
                string bestId = null; int bestScore = -1;
                foreach (Dictionary<string, object> song in data.OfType<Dictionary<string, object>>())
                {
                    var a = Dig(song, "attributes") as Dictionary<string, object>;
                    if (a == null) continue;
                    int score = Score(t, Str(a, "name"), Str(a, "artistName"), Str(a, "albumName"));
                    if (score > bestScore) { bestScore = score; bestId = Str(song, "id"); }
                }
                if (bestScore >= 150) return bestId;
            }
            return null;
        }

        /// <returns>enhanced HLS URL, "" when the song has none, null on failure.</returns>
        async Task<string> SongManifestUrlAsync(string songId, string token, CancellationToken ct)
        {
            var json = await GetAsync("https://amp-api.music.apple.com/v1/catalog/" + _storefront + "/songs/" + songId + "?extend=extendedAssetUrls", token, ct);
            if (json == null) return null;
            var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
            var data = Dig(root, "data") as object[];
            if (data == null || data.Length == 0) return null;
            return Dig(data[0] as Dictionary<string, object>, "attributes", "extendedAssetUrls", "enhancedHls") as string ?? "";
        }

        static SourceFormat BestLosslessVariant(string manifest)
        {
            SourceFormat best = null;
            foreach (var raw in manifest.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("#EXT-X-MEDIA:TYPE=AUDIO", StringComparison.Ordinal)) continue;
                var sr = Regex.Match(line, @"SAMPLE-RATE=""?(\d+)", RegexOptions.IgnoreCase);
                var bd = Regex.Match(line, @"BIT-DEPTH=""?(\d+)", RegexOptions.IgnoreCase);
                if (!sr.Success || !bd.Success) continue;
                int rate = int.Parse(sr.Groups[1].Value), bits = int.Parse(bd.Groups[1].Value);
                if (best == null || rate > best.Rate || (rate == best.Rate && bits > best.Bits))
                    best = new SourceFormat { Rate = rate, Bits = bits };
            }
            return best;
        }

        // ---- matching helpers (title must match; artist must overlap; album is a bonus)

        static int Score(TrackInfo t, string title, string artist, string album)
        {
            int ts = Compare(t.Title, title, 100, 70);
            if (ts < 0) return -1;
            int ar = Compare(t.Artist, artist, 50, 35);
            if (ar < 0 && SplitArtists(t.Artist).Overlaps(SplitArtists(artist))) ar = 25;
            if (ar < 0) return -1;
            int al = Math.Max(Compare(t.Album, album, 10, 5), 0);
            return ts + ar + al;
        }

        static int Compare(string expected, string actual, int exact, int partial)
        {
            var e = Norm(expected); var a = Norm(actual);
            if (e.Length == 0 || a.Length == 0) return -1;
            if (e == a) return exact;
            return e.Contains(a) || a.Contains(e) ? partial : -1;
        }

        static readonly string[] ArtistSeparators = { " feat. ", " feat ", " featuring ", " ft. ", " ft ", " & ", ",", ";", "/", " x ", " and " };

        static HashSet<string> SplitArtists(string artist)
        {
            var set = new HashSet<string>();
            if (string.IsNullOrWhiteSpace(artist)) return set;
            foreach (var part in artist.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                var n = Norm(part);
                if (n.Length > 0) set.Add(n);
            }
            return set;
        }

        static string PrimaryArtist(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist)) return "";
            var parts = artist.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0].Trim() : artist;
        }

        static string Norm(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder();
            foreach (var c in s.Normalize(NormalizationForm.FormKD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        static object Dig(Dictionary<string, object> d, params string[] path)
        {
            object cur = d;
            foreach (var k in path)
            {
                var dict = cur as Dictionary<string, object>;
                if (dict == null || !dict.TryGetValue(k, out cur)) return null;
            }
            return cur;
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v as string ?? "" : "";
        }
    }

    /// <summary>Reads sample rate / bit depth straight from MP4 (ALAC/AAC), WAV, AIFF or MP3 headers.</summary>
    static class AudioFileProbe
    {
        public static SourceFormat Probe(string path)
        {
            try
            {
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var head = new byte[12];
                    if (s.Read(head, 0, 12) < 12) return null;
                    var tag4 = Encoding.ASCII.GetString(head, 4, 4);
                    var tag0 = Encoding.ASCII.GetString(head, 0, 4);
                    SourceFormat f = null;
                    if (tag4 == "ftyp" || tag4 == "moov" || tag4 == "free" || tag4 == "wide") f = Mp4(s, 0, s.Length);
                    else if (tag0 == "RIFF") f = Wav(s);
                    else if (tag0 == "FORM") f = Aiff(s);
                    else f = Mp3(s);
                    return f != null && f.Rate > 0 ? f : null;
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // moov > trak > mdia > minf > stbl > stsd > (alac | mp4a | enca) [> alac magic cookie]
        static SourceFormat Mp4(Stream s, long start, long end)
        {
            long pos = start;
            while (pos + 8 <= end)
            {
                long content, boxEnd; string type;
                if (!Box(s, pos, end, out content, out boxEnd, out type)) break;
                switch (type)
                {
                    case "moov": case "trak": case "mdia": case "minf": case "stbl":
                        { var r = Mp4(s, content, boxEnd); if (r != null) return r; break; }
                    case "stsd":
                        { var r = Mp4(s, content + 8, boxEnd); if (r != null) return r; break; }
                    case "alac": case "mp4a": case "enca":
                        {
                            // AudioSampleEntry: 28-byte header, sample rate (16.16) at +24; nested boxes after.
                            var hdr = Read(s, content, 28);
                            int entryRate = hdr == null ? 0 : (hdr[24] << 8) | hdr[25];
                            var cookie = FindAlacCookie(s, content + 28, boxEnd);
                            if (cookie != null) return cookie;
                            if (type == "alac") return entryRate > 0 ? new SourceFormat { Rate = entryRate, Bits = 16 } : null;
                            return entryRate > 0 ? new SourceFormat { Rate = entryRate, Bits = 16 } : null; // lossy: 16 bit like macOS
                        }
                }
                pos = boxEnd;
            }
            return null;
        }

        static SourceFormat FindAlacCookie(Stream s, long start, long end)
        {
            long pos = start;
            while (pos + 8 <= end)
            {
                long content, boxEnd; string type;
                if (!Box(s, pos, end, out content, out boxEnd, out type)) break;
                if (type == "alac" && boxEnd - content >= 28)
                {
                    // full box (4) + cookie: frameLength u32, compatVersion u8, bitDepth u8, ... sampleRate u32 @ +20
                    var c = Read(s, content + 4, 24);
                    if (c != null)
                    {
                        int bits = c[5];
                        int rate = (c[20] << 24) | (c[21] << 16) | (c[22] << 8) | c[23];
                        if (rate > 0 && (bits == 16 || bits == 20 || bits == 24 || bits == 32))
                            return new SourceFormat { Rate = rate, Bits = bits };
                    }
                }
                pos = boxEnd;
            }
            return null;
        }

        static bool Box(Stream s, long pos, long limit, out long content, out long boxEnd, out string type)
        {
            content = boxEnd = 0; type = "";
            var h = Read(s, pos, 8);
            if (h == null) return false;
            long size = ((long)h[0] << 24) | ((long)h[1] << 16) | ((long)h[2] << 8) | h[3];
            type = Encoding.ASCII.GetString(h, 4, 4);
            content = pos + 8;
            if (size == 1)
            {
                var l = Read(s, pos + 8, 8);
                if (l == null) return false;
                size = 0;
                for (int i = 0; i < 8; i++) size = (size << 8) | l[i];
                content = pos + 16;
            }
            else if (size == 0) { boxEnd = limit; return true; }
            boxEnd = pos + size;
            return boxEnd >= content && boxEnd <= limit;
        }

        static SourceFormat Wav(Stream s)
        {
            long pos = 12;
            while (pos + 8 <= s.Length)
            {
                var h = Read(s, pos, 8);
                if (h == null) return null;
                int size = BitConverter.ToInt32(h, 4);
                if (Encoding.ASCII.GetString(h, 0, 4) == "fmt ")
                {
                    var f = Read(s, pos + 8, 16);
                    return f == null ? null : new SourceFormat { Rate = BitConverter.ToInt32(f, 4), Bits = BitConverter.ToInt16(f, 14) };
                }
                pos += 8 + size + (size & 1);
            }
            return null;
        }

        static SourceFormat Aiff(Stream s)
        {
            long pos = 12;
            while (pos + 8 <= s.Length)
            {
                var h = Read(s, pos, 8);
                if (h == null) return null;
                int size = (h[4] << 24) | (h[5] << 16) | (h[6] << 8) | h[7];
                if (Encoding.ASCII.GetString(h, 0, 4) == "COMM")
                {
                    var c = Read(s, pos + 8, 18);
                    if (c == null) return null;
                    int bits = (c[6] << 8) | c[7];
                    int exp = ((c[8] & 0x7F) << 8) | c[9];
                    ulong mant = 0;
                    for (int i = 0; i < 8; i++) mant = (mant << 8) | c[10 + i];
                    double rate = mant * Math.Pow(2, exp - 16383 - 63);
                    return new SourceFormat { Rate = (int)Math.Round(rate), Bits = bits };
                }
                pos += 8 + size + (size & 1);
            }
            return null;
        }

        static SourceFormat Mp3(Stream s)
        {
            long pos = 0;
            var id3 = Read(s, 0, 10);
            if (id3 != null && id3[0] == 'I' && id3[1] == 'D' && id3[2] == '3')
                pos = 10 + ((id3[6] & 0x7F) << 21 | (id3[7] & 0x7F) << 14 | (id3[8] & 0x7F) << 7 | (id3[9] & 0x7F));
            var buf = Read(s, pos, 4096);
            if (buf == null) return null;
            for (int i = 0; i + 3 < buf.Length; i++)
            {
                if (buf[i] != 0xFF || (buf[i + 1] & 0xE0) != 0xE0) continue;
                int ver = (buf[i + 1] >> 3) & 3, layer = (buf[i + 1] >> 1) & 3, idx = (buf[i + 2] >> 2) & 3;
                if (ver == 1 || layer == 0 || idx == 3) continue;
                int[] baseRates = { 44100, 48000, 32000 };
                int rate = baseRates[idx] / (ver == 3 ? 1 : ver == 2 ? 2 : 4);
                return new SourceFormat { Rate = rate, Bits = 16 };
            }
            return null;
        }

        static byte[] Read(Stream s, long pos, int count)
        {
            if (pos < 0 || pos + count > s.Length) return null;
            s.Position = pos;
            var b = new byte[count];
            int off = 0;
            while (off < count) { int n = s.Read(b, off, count - off); if (n <= 0) return null; off += n; }
            return b;
        }
    }
}
