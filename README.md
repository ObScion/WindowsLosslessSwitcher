# LosslessSwitcher for Windows

A Windows port of [vincentneo/LosslessSwitcher](https://github.com/vincentneo/LosslessSwitcher) (macOS).
It switches your output device's sample rate to match the song playing in Apple Music, automatically.
It lives in the system tray. It's a single ~60 KB exe with no installer and no dependencies (it uses the
.NET Framework 4.8 that ships with Windows).

## The menu (same as on macOS)

Left- or right-click the tray icon:

| Item | What it does |
|---|---|
| **96.0 kHz** / device name | Current rate (and bit depth when Bit Depth Switching is on) and the target device |
| Show Sample Rate / Show Icon | Tray shows a music note, or the rate in kHz (e.g. `44.1`, `192`) |
| Bit Depth Switching | Also switches bit depth (off by default, like macOS) |
| Prefer Closest Sample Rate Multiple | If the exact rate isn't supported, use the highest supported rate in the same 44.1/48 family below it |
| Selected Device ▸ | Default Device (follows Windows), or pin a specific output |
| Launch at Login | *Windows addition*: adds the app to HKCU `Run` |
| About ▸ | Version, build, Open Log |
| Scripting ▸ | Run a `.bat` / `.cmd` / `.ps1` / `.exe` on every rate change, with arguments `<sampleRateHz> [bitDepth]` |
| Quit LosslessSwitcher | |

## How it works on Windows

On macOS the app reads Apple Music's decoder logs. Windows has no equivalent log, so this port uses:

1. **The track change.** It watches Apple Music's Windows media session.
2. **Format detection**, retried every 2 s for about 10 s like the macOS timer:
   - *Local / cloud-library tracks:* it parses the header of the file Apple Music is downloading or
     caching in its `PlayCache` folder (ALAC cookie, WAV, AIFF, MP3).
   - *Streaming catalog tracks:* it looks the song up in Apple's public web catalog and reads the
     lossless master's sample rate and bit depth from its HLS manifest. This sends the song's
     title, artist and album to Apple.
   - If nothing matches, the device is left alone, as on macOS.
3. **Switching.** It probes which formats the device accepts (exclusive-mode `IsFormatSupported`),
   picks the nearest one (same rules as macOS), and sets the shared-mode format, which is the
   "Default Format" in the Sound control panel.

Apple Music rebuilds its audio stream a few seconds after a switch, so expect a short gap,
just as the macOS README warns. If the device is still silent after that while Apple Music says it's
playing, the app sends Apple Music one pause/play to restart it.

Settings: `%APPDATA%\LosslessSwitcher\settings.ini`. Log: `%APPDATA%\LosslessSwitcher\log.txt`.

## Requirements

- Windows 10 2004+ / Windows 11, with Apple Music from the Microsoft Store and Lossless turned on.
- For exact device capability detection, leave "Allow applications to take exclusive control of this
  device" enabled in the device's Advanced properties. Without it, the app just tries the target rate.

## Building

```powershell
.\build.ps1
```

You need a Roslyn `csc.exe` (Visual Studio or VS Build Tools). No .NET SDK or NuGet is required.
Output: `bin\LosslessSwitcher.exe`.

## Credits

- [LosslessSwitcher](https://github.com/vincentneo/LosslessSwitcher) by @vincentneo, the macOS original.
  This port copies its menu, its rate-selection rules and its retry timing.
- [WindowsLosslessSwitcher](https://github.com/jordanmgibson/WindowsLosslessSwitcher) by @jordanmgibson.
  The Apple Music for Windows techniques come from it: catalog manifest lookup, PlayCache layout and
  IPolicyConfig switching. This branch is a minimal rewrite; the full-featured app is on `main`.

## License

GPL-3.0, like both projects above.
