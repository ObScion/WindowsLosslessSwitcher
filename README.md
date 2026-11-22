# LosslessSwitcher for Windows (lite)

**Apple Music for Windows plays every song through your DAC at one fixed sample rate. This tray app
changes that rate to match the song.**

Play a 24-bit / 192 kHz Hi-Res Lossless track and your DAC switches to 192 kHz. When the next track is
44.1 kHz, it switches back down. Windows stops resampling, and you hear the file as it was mastered.

It's a faithful Windows port of [LosslessSwitcher](https://github.com/vincentneo/LosslessSwitcher) for
macOS: one small tray icon, one menu, no windows. The whole app is a **single exe under 100 KB** with no
installer, no runtime to install, no updater and no background services.

> This is the `lite` branch of a fork of
> [jordanmgibson/WindowsLosslessSwitcher](https://github.com/jordanmgibson/WindowsLosslessSwitcher).
> For the full-featured app, with a main window, toasts, a format cache and an auto-updater, use `main`
> or the upstream project.

## Download

1. Open the [latest release](https://github.com/ObScion/WindowsLosslessSwitcher/releases/latest) and
   download `LosslessSwitcher.exe`, or the zip, which also contains the license and this README.
2. Put it somewhere permanent, such as `%LOCALAPPDATA%\Programs\LosslessSwitcher\`, and run it.
   There's nothing to install. To uninstall, quit it from the tray and delete the file (and
   `%APPDATA%\LosslessSwitcher` if you want your settings gone too).

The exe isn't code-signed, so the first time you run it Windows SmartScreen may say it "protected your
PC". Click **More info → Run anyway**. If you'd rather not trust a download, [build it yourself](#building-from-source):
it takes one command.

## Using it

Run `LosslessSwitcher.exe`. A music note appears in the system tray. Play something in Apple Music.
To start it automatically, turn on **Launch at Login** in its menu. That points at wherever the exe
is now, so move it before you turn this on.

Click the icon to open the menu:

```
 192.0 kHz                          ← what your device is set to right now
 Speakers (iFi HD+ USB Audio)       ← the device being switched
 ─────────────────────────────
 Show Sample Rate                   ← show the rate in the tray instead of the note
 Bit Depth Switching                ← also match bit depth (off by default)
 Prefer Closest Sample Rate Multiple
 Selected Device               ▸    ← follow the Windows default, or pin one device
 Launch at Login
 About                         ▸    ← version, credits, license, source code, log
 Scripting                     ▸    ← run a script on every rate change
 Quit LosslessSwitcher
```

**Prefer Closest Sample Rate Multiple:** if your device can't play the track's exact rate, it uses
the highest supported rate in the same family. For example, 176.4 kHz falls back to 88.2 rather than 192.

**Scripting:** pick a `.bat`, `.cmd`, `.ps1` or `.exe`. It runs on every rate change with the new
rate (and bit depth, when known) as arguments, e.g. `script.bat 96000 24`. Use it to switch an AVR
input, a DSP preset or a room-correction profile.

## What to expect

- **A brief pause at the start of a track when the rate changes.** Windows has to rebuild the audio
  stream, and Apple Music reconnects on its own, typically in 0.2–0.7 s. The switch happens within about
  half a second of the track change, or about 30 ms for a song the app has seen before, so the pause
  lands at the start of the track. Your DAC may add a moment of its own while it re-locks to the new
  rate. Tracks at the same rate as the previous one start instantly. If Apple Music doesn't reconnect,
  the app sends it one pause/play.
- **Albums with mixed rates.** A "Hi-Res Lossless" badge means *some* tracks are hi-res, not all. On
  Steely Dan's *Aja*, for example, "Black Cow" streams at 24/192 while "Deacon Blues" streams at 24/48.
  The app follows each track, so you'll see the rate move within an album. That's correct.
- **If the format can't be determined, nothing changes.** The device stays where it is rather than
  guessing.

## How it works

The macOS original reads Apple Music's decoder logs. Apple Music for Windows has no such logs, so
this port works out each track's format in two ways:

1. **Streaming tracks:** it finds the song in Apple's public web catalog and reads the lossless
   stream list Apple publishes for it, such as `audio-alac-stereo-192000-24`. This is the same list
   Apple Music picks its stream from.
2. **Your own library tracks:** it reads the header of the file Apple Music is downloading or caching
   locally (ALAC, WAV, AIFF or MP3).

Then it checks which formats your device really accepts, picks the closest one, and sets it as the
device's default format, the same setting as *Sound Control Panel → Properties → Advanced*.

**Privacy:** catalog lookups send the song's title, artist and album to Apple's servers. Nothing is
sent anywhere else, and there's no telemetry.

## Requirements

- Windows 10 (2004 or later) or Windows 11.
- Apple Music from the Microsoft Store, with **Lossless** turned on (*Settings → Playback → Audio
  Quality*). Choose **Hi-Res Lossless** to get rates above 48 kHz.
- A DAC or audio interface you care about. Built-in laptop audio gains little.

**For the cleanest output:** set Apple Music's volume and the Windows volume to 100%, and turn off
*Audio enhancements* and spatial sound for your DAC. Leave *Allow applications to take exclusive control
of this device* **on**. The app uses it to discover which rates your DAC supports.

## Files

| | |
|---|---|
| Settings | `%APPDATA%\LosslessSwitcher\settings.ini` |
| Log | `%APPDATA%\LosslessSwitcher\log.txt` (also under *About → Open Log*) |
| Saved catalog results | `%APPDATA%\LosslessSwitcher\catalog-cache.tsv` (safe to delete) |

When a switch surprises you, check the log. It records each track, the streams Apple offers for it,
the format the app chose, how quickly it switched, and how long the dropout lasted.

## Building from source

```powershell
.\build.ps1
```

All you need is the C# compiler from Visual Studio or the free *Visual Studio Build Tools*. No .NET
SDK and no NuGet packages: it compiles against the .NET Framework 4.8 built into Windows. The output
is `bin\LosslessSwitcher.exe`. Run `.\build.ps1 -Package` to also produce the release zip
(`bin\LosslessSwitcher-<version>-win.zip`).

The source is six files in `src/`, about 1,700 lines in total. `tools\make-icon.ps1` regenerates
`src\app.ico`.

## Credits

- [LosslessSwitcher](https://github.com/vincentneo/LosslessSwitcher) by @vincentneo, the macOS
  original. This port copies its menu, its rate-selection rules and its retry timing.
- [WindowsLosslessSwitcher](https://github.com/jordanmgibson/WindowsLosslessSwitcher) by
  @jordanmgibson. It worked out how to do this on Windows: the catalog manifest lookup, the PlayCache
  layout and format switching through `IPolicyConfig`.

## License and copyright

- LosslessSwitcher: Copyright © 2022 Vincent Neo and contributors.
- WindowsLosslessSwitcher: Copyright © 2026 Jordan Gibson and contributors.
- This `lite` branch is a modified version of the works above. ObScion rewrote it in November 2026.

This program is free software under the GNU General Public License v3.0, the same license as both
projects above. See [LICENSE](LICENSE). It comes with no warranty: use it at your own risk.

The released exe is built from this branch, and each release links to the exact source it was built
from. Its *About* menu shows the copyright notices and links to the license and source.
