<div align="center">

# Stremio Community — Xbox

### Stremio Web in a controller-first Xbox app, with a native player.

**4K · HDR10 · Dolby Vision · Dolby Atmos · 100+ GB remuxes without a stutter**

[![Xbox Series X|S](https://img.shields.io/badge/Xbox-Series%20X%7CS-107C10?style=for-the-badge&logo=xbox&logoColor=white)](#-install)
[![Dev Mode](https://img.shields.io/badge/Dev%20Mode-UWP-5C2D91?style=for-the-badge)](#-install)
[![Dolby Vision](https://img.shields.io/badge/Dolby-Vision-P5-000000?style=for-the-badge&logo=dolby&logoColor=white)](#-features)
[![Dolby Atmos](https://img.shields.io/badge/Dolby-Atmos-000000?style=for-the-badge&logo=dolby&logoColor=white)](#-features)
[![License](https://img.shields.io/badge/License-GPL--2.0-blue?style=for-the-badge)](../LICENSE.md)

[Features](#-features) · [Controls](#-controls) · [Install](#-install) · [Limitations](#️-known-limitations)

**No streaming server. No PC. Just the XBOX.**

</div>

---

This is the **Stremio Community** port of the desktop app in this repository to Xbox Series X|S.
It wraps the official [Stremio Web](https://web.stremio.com) UI in a UWP app (WebView2) and, for
direct / debrid streams, hands playback to the **Xbox's own native media pipeline**: real HDR,
real Atmos, and huge files that just play.

> Unofficial. Not affiliated with or endorsed by Stremio.

## ✨ Features

<table>
<tr>
<td width="50%" valign="top">

**🎬 Native player**
Hardware decoding through the Xbox's own media pipeline. 4K at the file's real frame rate.

</td>
<td width="50%" valign="top">

**🌈 HDR10 & Dolby Vision (Profile 5)**
The TV switches into the right mode per file, automatically. DoVi 7/8 play as HDR10.

</td>
</tr>
<tr>
<td width="50%" valign="top">

**🔊 Dolby Atmos, DD+, AC3**
Passed through untouched to your soundbar or AVR.

</td>
<td width="50%" valign="top">

**🚀 Custom read buffer**
6 parallel connections and up to 768 MB of RAM read-ahead that follows the playhead. Built for
remuxes that make other players choke.

</td>
</tr>
<tr>
<td width="50%" valign="top">

**💬 Audio & subtitle menus**
Dedicated menus to pick an exact audio track (**Y**) or subtitle track (**X**) — embedded or from
addons — mid-play, with the currently playing one highlighted.

</td>
<td width="50%" valign="top">

**🎮 Made for the controller**
Full D-pad navigation, B to go back, a built-in cursor for the hard-to-reach bits, and the Xbox
keyboard for sign-in and search.

</td>
</tr>
</table>

**Formats:** MKV, MP4 · HEVC, H.264 · E-AC3 (Atmos), AC3, AAC and more.

## ⚡ Why not just use Edge?

| | Edge / web player | **Stremio Community** |
| --- | :---: | :---: |
| MKV · HEVC (x265) | ❌ "Stream is not supported" | ✅ Plays natively |
| 4K HDR10 · Dolby Vision | ❌ No mode switching | ✅ Auto resolution, refresh rate, HDR |
| Dolby Atmos · DD+ | ❌ Downmixed or silent | ✅ Bitstream passthrough |
| 100+ GB remuxes | ❌ Buffering, stutter | ✅ Smooth |
| Controller | ❌ Mouse-mode cursor | ✅ D-pad, back, cursor, keyboard |

## ⚠️ Known limitations

- **Dolby Vision Profile 7 / 8 play as HDR10.** The Xbox only outputs DV for Profile 5.
- **Dolby TrueHD sometimes has no sound.** Pick an E-AC3 / AC3 track if that happens.
- Built for direct / debrid links. Torrent streams need a streaming server.

## 🎮 Controls

**Browsing**

| Button | Action |
| --- | --- |
| D-pad / Left stick | Move |
| A | Select |
| B | Back |
| Left stick click | Cursor mode |
| View | Debug log |

**Player**

| Button | Action |
| --- | --- |
| A / Menu | Play / Pause |
| ◀ / ▶ | −10 s / +10 s |
| LB / RB | −60 s / +60 s |
| Y | Audio track menu |
| X | Subtitle menu |
| B | Stop |

## 📦 Install

1. Put your Xbox in Dev Mode and learn the Device Portal install:
   **[How to activate Dev Mode and install UWP apps](https://www.reddit.com/r/XboxRetailHomebrew/comments/1ktgw97/guide_how_to_activate_dev_mode_and_install_uwp/)**
2. Build the package (see below) and download the latest zip from **Releases**.
3. Device Portal → **Add** → pick the `.msix`, add every file from `Dependencies`, install.
4. Launch **Stremio Community**, sign in, add your addons as usual.

<details>
<summary><b>Build it yourself (Windows)</b></summary>

Visual Studio 2022 with the UWP workload and Windows SDK 10.0.22621.

1. `powershell -ExecutionPolicy Bypass -File scripts\New-DevCert.ps1` (once, creates the signing cert)
2. Open `StremioCommunity.sln`, **Release | x64**.
3. Deploy to the Xbox as a Remote Machine, or Publish → Create App Packages → Sideloading and install
   through Device Portal.

</details>

## 📁 Layout

```
xbox/
├── StremioCommunity.sln
├── scripts/
│   └── New-DevCert.ps1          # One-time signing certificate
└── StremioCommunity/
    ├── Package.appxmanifest     # UWP identity, capabilities (internet, private network, hevcPlayback)
    ├── StremioCommunity.csproj  # UWP project, WebView2 + WinRT packages
    ├── App.xaml(.cs)            # Entry point, layout scaling, pointer mode
    ├── MainPage.xaml(.cs)       # WebView2 host + native player + gamepad + debug log
    ├── Core/
    │   ├── BufferedHttpStream.cs# Multi-connection RAM read-ahead for large remuxes
    │   ├── DisplayModeManager.cs# HDMI 4K/HDR/refresh switching (adapted from Jellyfin for Xbox)
    │   ├── FileLog.cs           # Crash-surviving session log
    │   ├── GamepadInput.cs      # Controller polling
    │   └── PlayRequest.cs       # Stream hand-off from the page
    ├── Resources/shell.js       # Injected bridge: routing, native player, subtitles
    ├── Assets/                  # Logos & splash
    └── Properties/              # AssemblyInfo, runtime directives
```

## Credits

- [Jellyfin for Xbox](https://github.com/jellyfin/jellyfin-xbox): display mode switching and the
  WebView2 hosting approach (GPL-2.0, see [NOTICE.md](NOTICE.md)).
- [Stremio](https://www.stremio.com): the web app this wraps.
- The upstream **Stremio** Xbox UWP shell this project is adapted from.

<div align="center">
<sub>GPL-2.0 · see <a href="../LICENSE.md">LICENSE</a> and <a href="NOTICE.md">NOTICE.md</a></sub>
</div>
