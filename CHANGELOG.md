# Changelog

## [1.0.0] - 2026-10-31

### Added

- First Xbox Series X|S port of the Stremio Community desktop app in this repository.
- UWP app hosting Stremio Web (https://web.stremio.com) in WebView2 with a controller-first
  navigation layer (D-pad, A, B, built-in cursor, Xbox keyboard).
- Native Xbox player for direct / debrid streams: 4K HEVC, HDR10, Dolby Vision (Profile 5),
  Dolby Atmos / DD+ passthrough.
- Multi-connection RAM read-ahead (`Core/BufferedHttpStream.cs`) for large remuxes without stutter.
- Audio track menu (**Y**) and subtitle menu (**X**) with the currently playing track highlighted.
- HDMI display mode switching (resolution / refresh rate / HDR) per file
  (`Core/DisplayModeManager.cs`).
- Crash-surviving session log (`Core/FileLog.cs`) and an on-screen debug log (**View**).
- `scripts/New-DevCert.ps1` for generating the sideloading signing certificate.
