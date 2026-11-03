# Changelog

## [Unreleased]

### Added

- **Subtitle tracks sorted by language (todo #1, round 3):** the subtitle menu, the
  auto-select path, and the startup log now all use a single ordered list of subtitle tracks
  (sorted by `Language`, then by `Label`, case-insensitive). A new helper
  `GetOrderedSubtitleTrackIndices` (plus `CompareSubtitleTracks`) is the single source of
  truth, so the row the user highlights is the row that gets selected, and `_lastSubtitleSelection`
  stays consistent across reopenings.
- **8BitDo remote mapping (todo #4):** the user-captured `VirtualKey` codes (A = 0xC3, B = 0xC4,
  X = 0xC5, Y = 0xC6, D-pad Up/Down/Left/Right = 0xCB-0xCE) are now mapped to the same
  `PadButton` actions the Xbox gamepad uses. Face buttons (A/X/Y) are handled **before** the
  cursor-mode branch so they work in all modes (including cursor + keyboard up). B calls
  `HandleBack()` directly (it is not a `PadButton`; the Xbox routes it through `BackRequested`).
  The D-pad arrows are handled **after** the cursor-mode branch, so in cursor mode they are
  correctly swallowed (the Xbox left stick owns arrow navigation), while in normal D-pad mode
  they navigate menus as expected. A new `HandlePadButton` method holds the full button
  dispatch logic; the original `OnPadButton` remains as the Xbox gamepad entry point with the
  `_keyboardVisible` guard (the 8BitDo bypasses it, as its keys don't produce printable characters).
- **Preferred audio / subtitle language (todo #1):** two new settings rows — "Audio language"
  and "Subtitle language" — let the user pick a default language for each. On every video open
  (in `OnMediaOpened`), `ApplyPreferredLanguageTracks` scans the available tracks and auto-selects
  the first one whose `Language` starts with (or contains) the chosen code. Index 0 = "Auto
  (first track)" (no auto-selection, the default). Supported codes: es, en, fr, pt, de, it, ja,
  ko, zh, ru, ar. Persisted via `CommunitySettings.PreferredAudioLangIndex` / `PreferredSubLangIndex`.
- **Controls help overlay in the player (todo #2):** pressing **View (Guide)** while the native
  player is up opens a compact two-column list of all player controls (A = play/pause, Y = audio,
  X = subtitles, D-pad = navigate / ±10 s seek, LB/RB = ±60 s, cursor controls, etc.). Any button
  (including B) closes it. Implemented as a new `HelpOverlay` `Border` in `PlayerLayer` (XAML),
  populated by `OpenHelpOverlay` / `CloseHelpOverlay` (C#). View on the player no longer opens the
  debug panel — that shortcut is unchanged while browsing.
- **8BitDo remote diagnostics (todo #1):** the 8BitDo (and similar) remotes arrive as native
  accelerator keys, not as a `Gamepad`, so `GamepadInput` (which polls `Gamepad.Gamepads`) never
  sees them and the app ignored them. Every accelerator key (throttled per `VirtualKey`/`ScanCode`
  so held keys log once) is now logged with its `VirtualKey`, `ScanCode`, and extended flag —
  `ACCEL key vk=0x.. sc=.. ext=..` — so the exact code each remote button sends can be captured
  and mapped to an action in a follow-up. (Mapping the buttons requires the captured codes; the
  `shell.js` diagnostic logger also records the WebView2-side `key`/`code`/`keyCode`.)

### Changed

- **Session settings now persist across restarts (todo #2):** previously `OnLoaded`
  force-enabled the cursor on every launch, discarding the user's explicit choice. Now the
  saved `cursorMode` is respected, and the other session toggles that were reset each start are
  read back and re-saved on change: `nativePlayer` (debug panel Y), `attachSubtitles`
  (debug panel LB), `connections` (debug panel RB / settings), and `restartSeek`
  (debug panel Menu / settings). `pageZoom`, `systemCursor`, and the `CommunitySettings`
  values (volume, rate, mute, pause/resume, subtitles delay/speed/scale, buffer, HDR) already
  persisted and are unchanged.

### Fixed

- **On-screen keyboard typing in cursor mode + no more UI drift:** in cursor mode the app used to
  consume **every** key, so printable characters from the Xbox on-screen keyboard never reached a
  focused Stremio text field (search, login, …) — typing silently failed while D-pad mode worked.
  Simply letting every non-arrow key through made the opposite mistake: with the keyboard down and
  the left stick being used to move the cursor, unhandled keys still scrolled/moved the Stremio UI
  behind the cursor. The fix makes cursor-mode key handling depend on the on-screen keyboard state:
  - **Stick arrow keys** (Up/Down/Left/Right) are always swallowed — unhandled they move XAML focus
    and scroll the page behind the cursor.
  - **Keyboard up (typing):** every other key is left to the Windows input system, which converts it
    to the correct character for the active layout (Shift, symbols like @, Caps Lock) and delivers it
    to the focused field in WebView2 — the same path that already works in D-pad mode.
  - **Keyboard down (stick navigation):** everything else is swallowed, so nothing moves behind the
    cursor.

  An earlier attempt that re-injected characters via `document.execCommand('insertText')` was dropped
  because the Xbox reports the physical-position `VirtualKey` (e.g. `@` as `Q`) and UWP exposes no
  Shift state, so symbols came out wrong. Letting Windows do the key→character conversion avoids that
  entirely.
- **Preferred subtitle language actually applies (todo #2, round 3):** `ApplyPreferredLanguageTracks`
  used to disable **all** subtitle tracks and then enable the chosen one. The brief window with zero
  tracks presented let the player re-select the first track (often "none" or the original default),
  so the user's preference was silently discarded. Now the chosen track is enabled **first** and the
  others are disabled second, so the preference always sticks. A log line is emitted when no track
  matches the preferred language, so a miss is never silent.
- **Subtitles no longer disappear when adjusting delay / speed (todo #3, round 3):** stepping the
  subtitle delay or speed in the player's Subtitle menu used to call `TryRepresentSubtitleTracks`,
  which toggled the active track off and back on to "force" the renderer to pick up the new
  `SubDelay` / `SubRate`. The off-then-on window (a) dropped the subtitles visibly and (b) let the
  player re-select a different track, so the menu and the on-screen state diverged. The off-then-on
  hack is removed: `SubDelay` / `SubRate` are read by the renderer at render time, so the new values
  apply on the very next frame without any re-presentation. `TryRepresentSubtitleTracks` is gone.
  `SelectSubtitleMenu` now also enables the chosen track first and disables the others second,
  matching the auto-select path.

### Changed

- **Cursor (todo.md #1):** the on-screen dot is back to a native XAML `Ellipse` (the earlier
  WebView-SVG approach was removed — it caused a frozen "trail" of dots and added a slow
  C#→browser round-trip per move). Movement is now **delta-time integrated**: it measures the real
  elapsed time since the last gamepad tick and scales movement to it, so the cursor travels the
  correct distance per second and feels smooth regardless of the app's actual frame rate. Xbox
  caps non-game UWP apps at a ~30 Hz UI frame rate, which is the hard ceiling on how often any
  in-app cursor can repaint — the delta-time fix makes 30 fps move at full intended speed rather
  than half speed / choppy.
- **Subtitle delay/speed (todo.md #3):** application is now robust and observable. It reports
  clearly when no subtitle tracks are present, when the `SubDelay`/`SubRate` API is unavailable,
  or on error (previously it logged "applied" even when it did nothing), and it now **re-presents
  the active subtitle track** after the value changes so the pipeline actually re-renders with the
  new delay/rate.

### Added

- **Subtitle menu (X)** — adjustable **Subtitle delay** (±0.5 s, wraps −30→+30) and
  **Subtitle speed** (±0.05×, wraps 0.5→2.0) rows at the bottom. **A** steps the highlighted row;
  and now **D-pad Left/Right** step it down/up (todo.md #4). Applied live.

### Removed

- **Audio menu (Y) (todo.md #2):** the adjustable **Volume** row was removed — the audio menu now
  lists only the audio tracks.
- The **Subtitle delay** and **Subtitle speed** rows no longer appear in the browsing-mode
  **Settings** panel; they live only in the player's Subtitle menu.

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
