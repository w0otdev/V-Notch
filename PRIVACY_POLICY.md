# Privacy Policy — V-Notch

**Effective Date:** August 31, 2026 (revised)  
**Application Version:** 1.9.0  
**Developer:** rainaku  
**Contact:** [github.com/rainaku/V-Notch/issues](https://github.com/rainaku/V-Notch/issues)  

---

## 1. Introduction

V-Notch is a free, open-source desktop application for Windows that recreates a macOS-style notch and iPhone Dynamic Island experience. It displays now-playing media, battery and Bluetooth status, a file shelf, a camera preview, system volume and audio mixer, system resource monitor (CPU, RAM, GPU), clock, calendar, timer, Spotlight search launcher, and other ambient information with smooth animations and realistic Liquid Glass optics.

This Privacy Policy explains, in detail, exactly what data the application accesses, why it accesses it, where that data goes, and how long it is kept. It reflects the actual behavior of the application source code, which is publicly available for inspection at [github.com/rainaku/V-Notch](https://github.com/rainaku/V-Notch).

**Core principle:** V-Notch is strictly local-first. It contains no analytics, no telemetry, no advertising, no tracking identifiers, and requires no V-Notch account. It does not operate any backend server of its own. The only outbound network requests it makes are to public third-party services for specific opt-in or functional purposes: checking for application updates, fetching album artwork / lyrics / subtitles / Spotify Canvas for the media you are playing, and — if you explicitly enable it — showing the weather forecast. All of these are described in Section 4.

This policy uses the following terms:
- **"Local"** — data that stays on your computer and is never sent anywhere.
- **"Transient"** — data held in memory only while needed for display or processing, then discarded; never written to disk.
- **"Opt-in"** — a feature that is inactive until you explicitly enable or trigger it.

---

## 2. Summary at a Glance

| Capability | What it accesses | Leaves your device? | Stored on disk? |
|---|---|---|---|
| **Now-playing media** | Track title, artist, album, artwork, playback position, play state (Windows SMTC) | No (except artwork/lyrics/captions lookup — see §4) | No (transient in memory) |
| **Album artwork lookup** | Track title + artist sent as a search query | Yes — YouTube/Google, SoundCloud, Piped/Invidious | No (cached in memory and local source cache) |
| **Synced lyrics** | Track title + artist + duration sent as a query | Yes — lrclib.net, and api.lrcmux.dev as a fallback aggregator | No (transient in memory) |
| **YouTube subtitles / captions** | Video ID + caption track requests (YoutubeExplode) | Yes — YouTube | No (transient in memory) |
| **Spotify Canvas (opt-in)** | Spotify web session (`sp_dc`), track title + artist | Yes — Spotify, Musixmatch (fallback) | Session encrypted locally with Windows DPAPI |
| **Weather (opt-in)** | Approximate IP-based location (`ipwho.is`) or manual city name | Yes — `ipwho.is`, Open-Meteo | No (transient in memory) |
| **Update check & download** | Standard HTTP headers only | Yes — GitHub Releases API | Version info in memory; installer in temp directory on update |
| **Spotlight search & launcher** | Local app names, local file metadata (Windows Search / Everything), math expressions | No | Recent launch frequency stored locally (max 100 entries, see §5) |
| **Liquid Glass backdrop capture** | Screen pixels directly under the notch area (DXGI / Magnification API) | No | No (processed per-frame on GPU/CPU and discarded immediately) |
| **System hardware monitor** | CPU usage, RAM usage, GPU utilization (Windows performance counters / DXGI) | No | No (transient in memory) |
| **Camera preview (opt-in)** | Live camera frames | No | No (never recorded, captured, or saved) |
| **File Shelf** | File paths + basic file metadata (name, size, type) | No | File paths persisted locally in settings (see §5) |
| **System audio & mixer** | Read/adjust master and per-app audio endpoint volume (Core Audio) | No | No |
| **Media source detection** | Visible window titles; active browser URLs (UI Automation) | No | No (transient in memory; source cache on disk) |
| **Bluetooth & battery status** | Connected device name, type, battery level, state | No | No (transient in memory) |
| **Clipboard indicator & peek** | Clipboard format listener / copy event | No | No (clipboard content is never uploaded or saved) |
| **Privacy indicators** | Whether microphone, camera, or screen recording is active | No | No (transient in memory) |
| **Clock, Calendar, Timer** | Local system time, countdown timers, stopwatch | No | Timer presets in settings |
| **Gestures & mouse input** | Mouse movement/clicks over the notch | No | No |
| **Smart artwork crop** | On-device YOLO11n object detection (ONNX Runtime) | No | No (runs 100% locally) |

---

## 3. Data Accessed on Your Device

### 3.1 Now-Playing Media (Windows Media Session)

V-Notch uses the Windows System Media Transport Controls (SMTC) API to read metadata about media currently playing on your system — for example from Spotify, the YouTube/SoundCloud web players, Apple Music, Tidal, or any browser tab. The metadata includes track title, artist, album name, embedded album artwork, playback position, duration, and play/pause state.

This data is read continuously while media is playing, used to render the notch in real time, and held only in memory. It is never written to disk. The track title and artist may be sent to third-party services to look up artwork, lyrics, or subtitles — see Section 4.

### 3.2 Media Source Detection (Window Titles & Browser URLs)

To identify *where* media is playing (e.g. distinguishing a YouTube tab from a SoundCloud tab) and to fetch the correct artwork and lyrics, V-Notch performs two kinds of local inspection:

- **Window title scanning** — It enumerates the titles of visible top-level windows and keeps only those containing one of a fixed set of platform keywords: `spotify`, `youtube`, `soundcloud`, `facebook`, `tiktok`, `instagram`, `twitter` / `x`, `apple music`, `apple`, `music`, `twitch`, `discord`, `vesktop`, `netflix`, `tidal`, `deezer`, `bandcamp`, `bilibili`, `vimeo`, `crunchyroll`. The broader streaming and social-platform keywords exist to support detecting video/audio playback inside those sites' tabs or desktop clients — they are matched against the window title text only, not page content. Non-matching window titles are discarded immediately and never retained.
- **Browser URL reading** — For supported browsers (Chrome, Edge, Firefox, Brave, Opera, Vivaldi, Zen, Arc, Thorium, Floorp, Waterfox, and other Chromium/Gecko browsers), it uses the Windows UI Automation accessibility API to read the address bar and, if needed, open tabs, in order to find a media URL. Only URLs that look like media links are used.

This inspection happens entirely on your device. The titles and URLs are used transiently to drive media detection and artwork lookup, are cached only briefly in memory and local source cache, and are never stored to disk or transmitted as-is. (A derived value — the track title/artist — may be sent for artwork lookup as described in Section 4.)

### 3.3 Spotlight Search & Launcher (`Alt + Space`)

V-Notch features a built-in Spotlight search launcher that lets you find applications, search files, and calculate math expressions:

- **Application search** — Indexes local Start Menu shortcuts and installed Windows apps.
- **File search** — Queries your local Windows Search Index (via OLE DB) or local voidtools Everything instance (via Everything IPC local socket).
- **Inline Calculator** — Evaluates arithmetic and algebraic math expressions on-device using MathNet.Numerics.
- **Launch Ranking** — To provide quick access to frequent apps, V-Notch maintains a local ranking file (`%APPDATA%\V-Notch\spotlight-usage.json`) storing the last launched item ID, launch count, and timestamp (capped at 100 entries).

**All searches, queries, file paths, results, and calculations run 100% locally on your computer.** No search term or indexing data is ever sent to any external server.

### 3.4 Liquid Glass & Screen Backdrop Capture

V-Notch includes a Liquid Glass optical simulation engine that renders realistic glass refraction, chromatic aberration, edge bending, bevel, and blur effects matching macOS and Dynamic Island aesthetics.

- To calculate optical refraction, V-Notch samples a small region of the screen directly beneath the notch area using DirectX 11 (DXGI Desktop Duplication) or the Windows Magnification API.
- **Privacy safeguard:** Screen pixel sampling occurs solely inside local GPU/CPU memory on a per-frame basis to render the visual effect. Captured frames are immediately discarded once presented on screen. **No screen content is ever saved to disk, recorded, photographed, or transmitted over the network.**

### 3.5 System Hardware Monitor (CPU, RAM, GPU)

The System Monitor module reads system performance metrics (CPU usage percentage, physical RAM consumption, and GPU utilization) via standard Windows performance counters and DXGI adapter queries. This information is processed in memory for live widget display only and is never stored or transmitted.

### 3.6 Camera Preview (Opt-In)

V-Notch can show a live camera preview, but only when you explicitly open that feature. While active, camera frames are processed locally for on-screen display. **No frame is ever recorded, saved, photographed, or transmitted.** When you close the preview, the camera is released. When V-Notch's own camera preview is active, it suppresses its own "camera in use" privacy dot to avoid a redundant indicator.

### 3.7 File Shelf (Opt-In)

When you drag files onto the File Shelf, V-Notch records each file's path and basic file-system metadata (name, size, type) so it can display and manage the shelf. It uses a `FileSystemWatcher` on those locations to keep the shelf in sync if a file is moved or deleted. **The contents of your files are not opened, read, modified, or transmitted.** The list of file paths is saved locally in settings so the shelf persists between sessions (see Section 5).

### 3.8 System Audio Volume & Audio Mixer

V-Notch uses the Windows Core Audio API (via NAudio) to read the current system volume, monitor individual app audio sessions, and adjust them when you use the notch's volume control or audio mixer. No audio is recorded, intercepted, or captured; only numeric volume levels and session identities of active audio endpoints are read and set.

### 3.9 Bluetooth Device Status & Battery Levels

V-Notch watches for Bluetooth connect/disconnect events using the Windows device enumeration API in order to show a connection notification (for example, when your headphones connect) and accessory battery levels. It reads the device's display name, a category guess (headphones, speaker, keyboard, etc.), connection state, and battery percentage when available. This information is used transiently for the on-screen notification and widget, and is not stored or transmitted.

### 3.10 Clipboard Change Indicator & Peek

V-Notch registers a Windows clipboard format listener so it can show a brief "Copied" confirmation badge and optional preview when the clipboard changes. It reacts to the *event* that the clipboard was updated; this feature is used for visual feedback only and does not upload, log, or persist clipboard contents.

### 3.11 Privacy Indicators (Mic / Camera / Screen Capture)

Mirroring iOS/macOS behavior, V-Notch can display a small colored dot when your microphone, camera, or screen recording is in use by *any* application. This is a status reflection only — it indicates that a sensor is active, processes that status transiently in memory, and stores or transmits nothing.

### 3.12 Ambient Widgets (Clock, Calendar, Timer & Stopwatch)

V-Notch includes ambient widgets for clock, date, world time, interactive calendar, and a countdown timer / stopwatch. All calculations and timers run entirely locally on your device.

### 3.13 Gestures & Mouse Input

To support swipe and double-tap gestures on the notch (next/previous track, open shelf, play/pause), V-Notch monitors mouse movement and clicks in the region of the notch. This input is interpreted locally to recognize gestures and is never logged or transmitted.

### 3.14 On-Device Smart Thumbnail Cropping (ONNX)

If enabled, V-Notch uses a bundled YOLO11n object-detection model running locally through ONNX Runtime to intelligently crop wide artwork (centering on a face or subject). **All image analysis runs entirely on your device. No image, model input, or detection result is sent anywhere.** This feature requires no network connection.

---

## 4. Network Connections

V-Notch has no backend server and performs no analytics, telemetry, or user tracking. It makes outbound requests **only** to the following public third-party services, and **only** for the purposes described. No device identifiers or tracking tokens are attached; the optional Spotify Canvas feature uses your Spotify session only as described in Section 4.5.

### 4.1 Application Update Checks & Downloads — GitHub

- **Endpoint:** `https://api.github.com/repos/rainaku/V-Notch/releases/latest`
- **Why:** To detect whether a newer release of V-Notch is available.
- **Data sent:** Standard HTTP headers only, including `User-Agent: V-Notch-Updater` and a conditional `If-None-Match` (ETag) header for caching. No personal data is sent.
- **Data received:** Latest version tag, release notes, and installer download URL.
- **Frequency:** Throttled to at most once per 45 seconds; responses are cached in memory and revalidated with ETags.
- **Security & Integrity:** Update downloads enforce strict HTTPS, Authenticode signature validation, and SHA256 integrity checks.
- **Your control:** Downloading and installing an update happens **only** when you explicitly choose to. If you start an update, the installer (`V-Notch-Setup.exe`) is downloaded from GitHub Releases to your temporary folder and executed.

### 4.2 Album Artwork Lookup

When SMTC does not provide embedded artwork (common for browser-based playback), V-Notch tries to find a matching cover image. The track title and artist are used as search terms. Depending on the source, it may contact:

**YouTube / Google:**
- `https://www.youtube.com/results?...` — scraping the public search page for a matching video.
- `https://www.youtube.com/oembed?...` — validating a video and retrieving its title/thumbnail.
- `https://i.ytimg.com/...` — fetching the thumbnail image.
- `https://www.googleapis.com/youtube/v3/search` — the official YouTube Data API, used **only if** you have supplied your own API key. No key ships with the app.

**Piped / Invidious (privacy-friendly YouTube front-ends, used as fallbacks):**
- Public instances such as `pipedapi.kavin.rocks`, `pipedapi.adminforge.de`, `vid.puffyan.us`, `invidious.fdn.fr`, and similar. These are third-party community-run services contacted only if the primary lookup fails.

**SoundCloud:**
- The SoundCloud oEmbed endpoint, to retrieve the artwork URL for a SoundCloud track.

**Data sent:** Track title and artist (as a search query) and standard browser-like HTTP headers. **No user-identifiable information is included.** Retrieved images are held in memory for display and are not written to disk.

### 4.3 Synced Lyrics — LRCLIB and lrc mux

V-Notch tries two independent lyrics providers, in order, and stops as soon as one returns a result:

- **LRCLIB** — `https://lrclib.net/api/get?...` (exact match) and its search endpoint (fuzzy match). **Data sent:** track title, artist name, and track duration as query parameters, plus a `User-Agent` identifying V-Notch.
- **lrc mux** — `https://api.lrcmux.dev/get?...`, used as a fallback aggregator when LRCLIB has no match. **Data sent:** track title, artist name, and track duration as query parameters, plus a `User-Agent` identifying V-Notch. lrc mux is a third-party lyrics aggregation service with its own upstream sources; V-Notch does not control which upstream provider it queries internally.

**Data received (both):** Synced lyric lines, used transiently in memory for display and never written to disk. No personal data is sent to either provider.

### 4.4 YouTube Subtitles & Captions — YoutubeExplode

- **Endpoint:** Public YouTube video caption endpoints via YoutubeExplode library.
- **Why:** To fetch timed closed captions/subtitles when playing YouTube videos and subtitles are enabled.
- **Data sent:** YouTube video ID and standard HTTP headers. No user account data or personal identifiers are sent.
- **Data received:** Timed caption text, used transiently in memory for display.

### 4.5 Spotify Canvas (Opt-In)

When you choose **Connect Spotify**, V-Notch opens Spotify's own sign-in page in a temporary Microsoft Edge WebView2 profile. After sign-in, it reads only Spotify's `sp_dc` session cookie, clears the temporary browser profile, and stores the cookie encrypted with Windows DPAPI for the current Windows user. It is never sent to a V-Notch server or any analytics server.

While Canvas is enabled, the session is sent to Spotify (`open.spotify.com`) to obtain a short-lived access token. V-Notch sends the current track title and artist with that token to Spotify's catalog service (`api-partner.spotify.com`) to resolve the Spotify track ID. If that lookup is unavailable, it sends the title, artist, and duration to Musixmatch (`apic-desktop.musixmatch.com`) as a fallback. It then requests Canvas metadata from Spotify (`spclient.wg.spotify.com`), and streams the video from Spotify's `*.scdn.co` content delivery network. Public Spotify web-player assets (`open.spotify.com`, `open.spotifycdn.com`) may be fetched to keep the catalog query compatible; those refresh requests contain no session or track metadata. The rotating token secret used by Spotify's web player is downloaded from the public `xyloflake/spot-secrets-go` GitHub repository; no user data is sent with that request.

You can disconnect Spotify at any time in Settings. This removes the stored session from V-Notch. If authentication fails or no Canvas exists, V-Notch uses the normal lyrics background.

### 4.6 Weather (Opt-In)

When you enable the weather widget, V-Notch makes network requests **only after** you have explicitly turned the feature on. The weather widget is **off by default**; no weather-related requests are made on a fresh install until you enable it.

- **IP-based location (default):** `https://ipwho.is/` — Your approximate location (latitude, longitude, city) is resolved from your IP address. This is **not** your precise GPS location; it is a coarse geographic approximation based on your IP's registered region. Only the HTTPS endpoint is used.
- **Manual city (optional):** If you enter a city name manually, `https://geocoding-api.open-meteo.com/v1/search` is used to resolve it to coordinates. When a manual city is provided, no IP lookup is performed.
- **Weather forecast:** `https://api.open-meteo.com/v1/forecast` — The latitude/longitude (from either IP lookup or manual city entry) is sent to Open-Meteo to retrieve the current temperature, weather code, daily high/low, and timezone.
- **Frequency:** Every 15 minutes while the weather widget is active. Requests are cancelled when you turn the feature off.

All three endpoints are third-party services with their own privacy policies:
- [ipwho.is/privacy](https://ipwho.is/privacy)
- [open-meteo.com/privacy](https://open-meteo.com/privacy)

**Data sent:** Your IP address (to ipwho.is), or a city name (to Open-Meteo geocoding), and latitude/longitude coordinates (to Open-Meteo forecast). No other personal data is included.

### 4.7 Third Parties

The services above (Spotify, GitHub, Google/YouTube, Piped/Invidious instances, SoundCloud, LRCLIB, ipwho.is, and Open-Meteo) are independent third parties with their own privacy policies. When V-Notch contacts them, your IP address is visible to that service as with any normal web request. V-Notch does not control and is not responsible for how those services handle requests. If you prefer to avoid these lookups, you can disable artwork/lyrics/Canvas/weather features and update checks, or block the app's network access.

---

## 5. Local Data Storage

All persistent data created by V-Notch lives exclusively on your local device.

### 5.1 Settings (`%APPDATA%\V-Notch\settings.json`)

Stores your preferences: notch size and position, visual style and Liquid Glass options, notification toggles, language, startup behavior, File Shelf contents (file paths), and feature flags. Settings may contain a YouTube API key only if you explicitly provide one and a Spotify session only if you choose Connect Spotify. Both values are encrypted using Windows DPAPI (Data Protection API) before they are written to disk. The encrypted values are tied to the current Windows user account and cannot be decrypted by another user or on another machine. If DPAPI is unavailable, these sensitive values are not saved.

### 5.2 Spotlight Usage History (`%APPDATA%\V-Notch\spotlight-usage.json`)

Stores your recent Spotlight launches (application IDs, titles, targets, launch count, and timestamp) to provide quick access to frequent items. This store is capped at 100 entries, runs purely on your device, and is never transmitted anywhere.

### 5.3 Media Source Cache (`%APPDATA%\V-Notch\source_cache.json`)

Stores an LRU mapping (up to 500 entries) between media identity titles and resolved sources (e.g. YouTube/SoundCloud) to avoid redundant online searches for songs you play repeatedly.

### 5.4 Diagnostic Log (`vnotch-debug.log`)

Located in the application's program folder, this log records technical application events and errors to help diagnose problems. Because of what it logs, it can incidentally contain the titles/artists of tracks you played, lyrics-provider queries, and matched window titles (e.g. a browser tab title) — this is the same information already described in Sections 3 and 4, just also written locally for debugging. It is automatically rotated when it reaches about 5 MB. **This log is never transmitted anywhere** — it stays on your machine, is not uploaded with crash or update requests, and you may delete it at any time.

### 5.5 Optional ONNX Model

If present, the smart-crop model file (`yolo11n.onnx`) is stored locally alongside the app and is used purely for on-device image analysis.

You can remove all stored data at any time by deleting the `%APPDATA%\V-Notch\` folder and the application directory.

---

## 6. Data V-Notch Does NOT Collect

V-Notch does **not**:
- collect, sell, or share personal information with third parties for marketing;
- run analytics, telemetry, behavioral tracking, or device fingerprinting;
- send automated crash reports or usage statistics;
- record audio, video, or screen content;
- read, upload, or back up the contents of your files;
- access precise device GPS location;
- create user accounts, profiles, or advertising identifiers;
- store or upload clipboard contents;
- send local Spotlight search queries or file index data over any network.

---

## 7. Permissions & APIs Reference

| Permission / API | Purpose | Required? |
|---|---|---|
| **Media Session (SMTC)** | Show now-playing media metadata & playback control | Yes (core feature) |
| **Audio Endpoint (Core Audio)** | Read and adjust system master & app volume levels | Yes (core feature) |
| **DirectX 11 / DXGI / Magnification** | Local screen backdrop sampling for Liquid Glass refraction | Optional (visual effect) |
| **Windows Search / Everything IPC** | Local file and application search in Spotlight (`Alt + Space`) | Optional |
| **Internet Access** | Update checks, artwork & lyrics lookup, weather forecast | Optional |
| **Camera (DirectShow / MediaFoundation)** | Live camera preview inside the notch | Opt-in |
| **File System Access** | File Shelf drag-and-drop file staging | Opt-in |
| **UI Automation** | Detect active media playback URLs in supported web browsers | Used for media detection |
| **Bluetooth (Device Enumeration)** | Device connection/disconnection alerts & accessory battery | Optional |
| **Clipboard Format Listener** | "Copied" confirmation animation & preview badge | Optional |
| **Windows Performance Counters** | Real-time CPU/RAM/GPU system monitor stats | Optional |

---

## 8. Security

V-Notch runs with standard user privileges and does not require administrator rights for normal operation. Administrator elevation is requested only when installing an update (to run the installer). All sensitive stored credentials (Spotify `sp_dc` cookie, YouTube API key) are encrypted with Windows DPAPI. All update packages are signed and verified with Authenticode and SHA256 hashes over secure HTTPS. Because the application is fully open source, anyone may audit exactly what it does at [github.com/rainaku/V-Notch](https://github.com/rainaku/V-Notch).

---

## 9. Children's Privacy

V-Notch does not collect personal data from anyone, including children, and does not direct any content toward children specifically. It is suitable for all ages.

---

## 10. International Use

V-Notch processes data locally on your device. The only data that crosses a network is the limited request data described in Section 4, sent to the third-party services listed there, which may operate in various countries. No personal data is transferred or stored by the developer.

---

## 11. Changes to This Policy

This Privacy Policy may be updated as features change. Material changes will be reflected in this document, in the application changelog, and through an updated effective date and version number above. Continued use of the application after an update constitutes acceptance of the revised policy.

**Revision notes (this update):** clarified that synced lyrics may also be fetched from `api.lrcmux.dev` as a fallback to LRCLIB; corrected the supported-browser list for URL detection to include Zen Browser; documented the full window-title keyword set used for media source detection (including Facebook, TikTok, Instagram, and Twitter/X, used only to detect playback inside those sites' tabs); and clarified that the local diagnostic log can incidentally contain track/window title data already covered elsewhere in this policy.

---

## 12. Contact

Questions, concerns, or data-related requests can be raised by opening an issue at:  
[https://github.com/rainaku/V-Notch/issues](https://github.com/rainaku/V-Notch/issues)