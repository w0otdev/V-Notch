# Privacy Policy - V-Notch

**Effective date:** October 31, 2026

**Previous published version:** October 24, 2026

**Scope:** Current source tree as of October 31, 2026 (v2.2.1); feature availability depends on the installed build.

**Developer:** w0otdev  
**Contact:** [github.com/w0otdev/V-Notch/issues](https://github.com/w0otdev/V-Notch/issues)  

---

## 1. Introduction

V-Notch is a free, open-source desktop application for Windows that recreates a macOS-style notch and iPhone Dynamic Island experience. It displays now-playing media, battery and Bluetooth status, a file shelf, a camera preview, system volume and audio mixer, system resource monitor (CPU, RAM, GPU), clock, calendar, timer, Spotlight search launcher, and other ambient information with smooth animations and realistic Liquid Glass optics.

This Privacy Policy explains, in detail, exactly what data the application accesses, why it accesses it, where that data goes, and how long it is kept. It reflects the actual behavior of the application source code, which is publicly available for inspection at [github.com/w0otdev/V-Notch](https://github.com/w0otdev/V-Notch).

**Core principle:** V-Notch is designed to be local-first. It contains no analytics, no telemetry, no advertising, no tracking identifiers, and requires no V-Notch account. It does not operate any backend server of its own. The application documentation identifies outbound network requests to third-party services for specific opt-in or functional purposes: checking for application updates, fetching album artwork / lyrics / subtitles / Spotify Canvas for the media you are playing, and — if you explicitly enable it — showing the weather forecast. Optional Spotlight AI sends messages and conversation context to your selected AI provider and can retrieve a DeepSeek balance on request. These are described in Section 4; the list and actual requests must be checked against the shipped binary and installed web components.

This document describes application-side practices and is not a representation that independent websites, operating-system components, webviews, content delivery networks, or user-installed modifications do not collect data. Verify feature availability and behavior against your installed build.

This policy uses the following terms:
- **"Local"** — data processed on the computer and not intentionally transmitted by that feature, subject to the network exceptions explicitly described.
- **"Transient"** — data held in memory only while needed for display or processing, then discarded; never written to disk.
- **"Opt-in"** — a feature that is inactive until you explicitly enable or trigger it.

---

## 2. Summary at a Glance

| Capability | What it accesses | Leaves your device? | Stored on disk? |
|---|---|---|---|
| **Now-playing media** | Track title, artist, album, artwork, playback position, play state (Windows SMTC) | No (except artwork/lyrics/captions lookup — see §4) | No (transient in memory) |
| **Album artwork lookup** | Track title + artist sent as a search query | Yes — YouTube/Google, SoundCloud, Piped/Invidious | Yes: source mapping may be stored locally (§5.3); image retention depends on component cache |
| **Synced lyrics** | Track title + artist + duration sent as a query | Yes — lrclib.net, and api.lrcmux.dev as a fallback aggregator | No (transient in memory) |
| **YouTube subtitles / captions** | Video ID + public caption track requests | Yes - YouTube | No (transient in memory) |
| **Spotify Canvas (opt-in)** | Spotify web session (`sp_dc`), track title + artist | Yes — Spotify, Musixmatch (fallback) | Session encrypted locally with Windows DPAPI |
| **Weather (opt-in)** | Approximate IP-based location (`ipwho.is`) or manual city name | Yes — `ipwho.is`, Open-Meteo | No (transient in memory) |
| **Update check & download** | Standard HTTP headers only | Yes - GitHub Releases API & Atom feed | Version info in memory; installer in temp directory on update |
| **Spotlight AI (opt-in)** | Submitted messages, recent context, API key, selected model | Yes — selected AI provider (§4.8) | Keys and chat history encrypted locally (§5.6) |
| **Live translation (opt-in)** | Selected text exposed by UI Automation; clipboard text only on an explicit command | No text upload; optional model download contacts Hugging Face | Model/configuration only; translation cache stays in RAM |
| **Spotlight search & launcher** | Local app names, local file metadata (native indexer / Windows Search / Everything), math expressions | No | Recent launch frequency stored locally (max 100 entries, see §5) |
| **Liquid Glass backdrop capture** | Screen pixels directly under the notch area (DXGI / Magnification API) | No | No (processed per-frame on GPU/CPU and discarded immediately) |
| **System hardware monitor** | CPU usage, RAM usage, GPU utilization (Windows performance counters / DXGI) | No | No (transient in memory) |
| **Camera preview (opt-in)** | Live camera frames | No | No (never recorded, captured, or saved) |
| **File Shelf** | File paths + basic file metadata (name, size, type) | No | File paths persisted locally in settings (see §5) |
| **System audio & mixer** | Read/adjust master and per-app audio endpoint volume (Core Audio) | No | No |
| **Media source detection** | Visible window titles; active browser URLs (UI Automation) | No | Potentially: derived media-title/source mapping in local cache (§5.3); diagnostic logs if enabled (§5.4) |
| **Bluetooth & battery status** | Connected device name, type, battery level, state | No | No (transient in memory) |
| **Clipboard history, indicator & peek** | Copied text, HTML/RTF, images, files/folders and source-app metadata | No automatic upload by Clipboard history | Yes — local history at `%LOCALAPPDATA%\V-Notch\Clipboard\` (§3.10, §5.7) |
| **Privacy indicators** | Whether microphone, camera, or screen recording is active | No | No (transient in memory) |
| **Clock, Calendar, Timer & White Noise** | Local system time, countdown timers, stopwatch, synthesized ambient audio | No | Timer presets in settings |
| **Gestures & mouse input** | Mouse movement/clicks over the notch | No | No |
| **Smart artwork crop** | On-device YOLOX-Nano object detection (ONNX Runtime) | No | No (runs 100% locally) |

### Service & Storage Overview

| Service | Purpose |
| --- | --- |
| GitHub Releases API | Update checks |
| LRCLIB / lrc mux | Synced lyrics |
| YouTube / SoundCloud | Public thumbnails and captions |
| Spotify Web Services | Experimental Canvas, off by default; explicit opt-in; session encrypted with Windows DPAPI |
| Open-Meteo / ipwho.is | Optional weather |
| OpenAI / Google Gemini / Anthropic Claude / DeepSeek | Optional AI messages and conversation context; DeepSeek balance refresh on request |

Settings and several local caches are stored at `%APPDATA%\V-Notch\`. Encrypted AI chat history is stored separately at `%LOCALAPPDATA%\V-Notch\spotlight-chats.enc` (legacy path `%LOCALAPPDATA%\VNotch\spotlight-chats.enc`). Other component caches and temporary files are described in Section 5. Local Spotlight Search does not upload queries; Spotlight AI sends the content you submit to your selected provider.

**Third-party media:** Canvas uses unofficial Spotify endpoints and requires acknowledgement before sign-in or requests. Spotify may restrict access; opt-in does not grant platform permission. YouTube captions must be accessed and used according to YouTube's terms and applicable law. Fair use is assessed case by case. Read the [external-platform notices](TERMS_OF_SERVICE.md#4-external-platforms-and-content) before enabling these features.

> [!NOTE]
> **API Key Safety:** User-provided API credentials (OpenAI, Gemini, Claude, DeepSeek, YouTube) are encrypted locally with Windows DPAPI. V-Notch maintains no intermediary servers. Maintainers assume no liability for third-party billing, quota exhaustion, or credentials leaked from user environments or pre-release testing builds. Please configure spending caps on your provider accounts.

Privacy dots use capture reports from Windows and local recording signals from Bandicam, OBS Studio, and FFmpeg. They provide a status cue and do not detect every capture method.

---

## 3. Data Accessed on Your Device

### 3.1 Now-Playing Media (Windows Media Session)

V-Notch uses the Windows System Media Transport Controls (SMTC) API to read metadata about media currently playing on your system — for example from Spotify, the YouTube/SoundCloud web players, Apple Music, Tidal, or any browser tab. The metadata includes track title, artist, album name, embedded album artwork, playback position, duration, and play/pause state.

This data is read continuously while media is playing, used to render the notch in real time, and held only in memory. It is never written to disk. The track title and artist may be sent to third-party services to look up artwork, lyrics, or subtitles — see Section 4.

### 3.2 Media Source Detection (Window Titles & Browser URLs)

To identify *where* media is playing (e.g. distinguishing a YouTube tab from a SoundCloud tab) and to fetch the correct artwork and lyrics, V-Notch performs two kinds of local inspection:

- **Window title scanning** — It enumerates the titles of visible top-level windows and keeps only those containing one of a fixed set of platform keywords: `spotify`, `youtube`, `soundcloud`, `instagram`, `twitter` / `x`, `apple music`, `apple`, `music`, `twitch`, `discord`, `vesktop`, `netflix`, `tidal`, `deezer`, `bandcamp`, `bilibili`, `vimeo`, `crunchyroll`. The broader streaming and social-platform keywords exist to support detecting video/audio playback inside those sites' tabs or desktop clients — they are matched against the window title text only, not page content. Window titles may be briefly inspected; handling of matching titles and diagnostic logging is explained below and in §5.4.
- **Browser URL reading** — For supported browsers (Chrome, Edge, Firefox, Brave, Opera, Vivaldi, Zen, Arc, Thorium, Floorp, Waterfox, and other Chromium/Gecko browsers), it uses the Windows UI Automation accessibility API to read the address bar and, if needed, open tabs, in order to find a media URL. Only URLs that look like media links are used.

Browser URL inspection is blocked until loaded settings grant permission. The scanner reads that permission from memory, without rereading or watching the settings file. Disabling **Browser URL Inspection** prevents cached URLs or earlier in-flight results from being returned. An accessibility call already executing may finish, but no new inspection starts while permission is disabled. This toggle controls browser accessibility inspection; window-title scanning remains separate.

This inspection happens entirely on your device. The titles and URLs are used transiently to drive media detection and artwork lookup, are cached only briefly in memory and local source cache, and may appear in a local diagnostic log when enabled (see §5.4); the application is not designed to upload raw window titles or full browser URLs. (A derived value — the track title/artist — may be sent for artwork lookup as described in Section 4.)

### 3.3 Spotlight Search & Launcher (`Alt + Space`)

V-Notch features a built-in Spotlight search launcher that lets you find applications, search files, and calculate math expressions:

- **Application search** — Indexes local Start Menu shortcuts and installed Windows apps.
- **File search**: Queries the built-in local file indexer, your local Windows Search Index (via OLE DB), or a local voidtools Everything instance (via Everything IPC local socket).
- **Inline Calculator**: Evaluates arithmetic and algebraic math expressions on-device through the native C++ evaluator.
- **Launch Ranking** — To provide quick access to frequent apps, V-Notch maintains a local ranking file (`%APPDATA%\V-Notch\spotlight-usage.json`) storing the last launched item ID, launch count, and timestamp (capped at 100 entries).

**Spotlight Search processes app/file queries and calculations locally.** The separate AI mode sends messages you explicitly submit and conversation context as described in §4.8.

### 3.4 Liquid Glass & Screen Backdrop Capture

V-Notch includes a Liquid Glass optical simulation engine that renders realistic glass refraction, chromatic aberration, edge bending, bevel, and blur effects matching macOS and Dynamic Island aesthetics.

- To calculate optical refraction, V-Notch samples a small region of the screen directly beneath the notch area using DirectX 11 (DXGI Desktop Duplication) or the Windows Magnification API.
- **Privacy safeguard:** Screen pixel sampling occurs solely inside local GPU/CPU memory on a per-frame basis to render the visual effect. Captured frames are immediately discarded once presented on screen. **V-Notch does not intentionally persist or upload these sampled frames; operating-system/graphics components may manage their own memory or caches.**

### 3.5 System Hardware Monitor (CPU, RAM, GPU)

The System Monitor module reads system performance metrics (CPU usage percentage, physical RAM consumption, and GPU utilization) via standard Windows performance counters and DXGI adapter queries. This information is processed in memory for live widget display only and is never stored or transmitted.

### 3.6 Camera Preview (Opt-In)

V-Notch can show a live camera preview, but only when you explicitly open that feature. While active, camera frames are processed locally for on-screen display. **No frame is ever recorded, saved, photographed, or transmitted.** When you close the preview, the camera is released. When V-Notch's own camera preview is active, it suppresses its own "camera in use" privacy dot to avoid a redundant indicator.

### 3.7 File Shelf (Opt-In)

When you drag files onto the File Shelf, V-Notch records each file's path and basic file-system metadata (name, size, type) so it can display and manage the shelf. It uses a `FileSystemWatcher` on those locations to keep the shelf in sync if a file is moved or deleted. **The contents of your files are not opened, read, modified, or transmitted.** The list of file paths is saved locally in settings so the shelf persists between sessions (see Section 5).

### 3.8 System Audio Volume & Audio Mixer

V-Notch uses the Windows Core Audio API directly in native code to read the current system volume, monitor individual app audio sessions, and adjust them when you use the notch's volume control or audio mixer. No audio is recorded, intercepted, or captured; only numeric volume levels and session identities of active audio endpoints are read and set.

### 3.9 Bluetooth Device Status & Battery Levels

V-Notch watches for Bluetooth connect/disconnect events using the Windows device enumeration API in order to show a connection notification (for example, when your headphones connect) and accessory battery levels. It reads the device's display name, a category guess (headphones, speaker, keyboard, etc.), connection state, and battery percentage when available. This information is used transiently for the on-screen notification and widget, and is not stored or transmitted.

### 3.10 Clipboard History, Change Indicator & Peek

V-Notch registers a Windows clipboard format listener for the "Copied" badge, preview and persistent Clipboard history. While capture is active (text, image and file capture are enabled by default), it reads supported clipboard content and saves local copies: plain text, HTML/RTF, images and the contents of copied files/folders, together with timestamps and source application name/executable path. History remains available after restarting the app. Items can also be imported by drag and drop.

Dropping an image represented by an HTTP(S) URL can download it from the image host, which receives the requested URL, your IP address and ordinary connection metadata. These explicit imports use the same network permission as online artwork lookup: Strict Local-Only Mode or disabling online artwork lookup blocks the request, and withdrawing permission cancels ongoing response reads. Downloads have a 20 MiB limit, including when the server does not declare a size. Embedded image data is processed locally.

Before reading content for history, V-Notch skips clipboard items carrying `ExcludeClipboardContentFromMonitorProcessing` or `Clipboard Viewer Ignore`, or a `CanIncludeInClipboardHistory` DWORD of zero. Unreadable history markers are also skipped. These safeguards depend on the source app providing the markers; they cannot identify every password or sensitive copy. Clipboard history does not automatically upload its contents. Content you paste into an online AI message and submit is sent as described in §4.8.

Pause capture in the Clipboard tray or disable individual text/image/file capture types in Clipboard settings to stop new captures. These controls do not erase existing history. Storage, retention and deletion are described in §5.7.

### 3.11 Privacy Indicators (Mic / Camera / Screen Capture)

Mirroring iOS/macOS behavior, V-Notch can display a small colored dot when your microphone, camera, or screen recording is in use by *any* application. This is a status reflection only — it indicates that a sensor is active, processes that status transiently in memory, and stores or transmits nothing.

### 3.12 Ambient Widgets (Clock, Calendar, Timer, Stopwatch & White Noise)

V-Notch includes ambient widgets for clock, date, world time, interactive calendar, a countdown timer / stopwatch, and an ambient white noise generator. All calculations, timers, and audio synthesis run locally on your device.

### 3.13 Gestures & Mouse Input

To support swipe and double-tap gestures on the notch (next/previous track, open shelf, play/pause), V-Notch monitors mouse movement and clicks in the region of the notch. This input is interpreted locally to recognize gestures and is never logged or transmitted.

### 3.14 On-Device Smart Thumbnail Cropping (ONNX)

If enabled, V-Notch uses a bundled YOLOX-Nano object-detection model running locally through ONNX Runtime to crop wide artwork around a face or subject. **All image analysis runs on your device. No image, model input, or detection result is sent anywhere.** This feature requires no network connection.

---

## 4. Network Connections

V-Notch has no backend server and performs no analytics, telemetry, or user tracking. It makes outbound requests **only** to the following public third-party services, and **only** for the purposes described. No device identifiers or tracking tokens are attached; the optional Spotify Canvas feature uses your Spotify session only as described in Section 4.5.

### 4.1 Application Update Checks & Downloads — GitHub

- **Endpoints:** `https://api.github.com/repos/w0otdev/V-Notch/releases/latest`, `https://api.github.com/repos/w0otdev/V-Notch/releases` (when checking pre-releases or release notes), and `https://github.com/w0otdev/V-Notch/releases.atom` (fallback when the REST API is rate-limited).
- **Why:** To detect whether a newer release of V-Notch is available.
- **Data sent:** Standard HTTP headers, including `User-Agent: V-Notch-Updater` and a conditional `If-None-Match` (ETag) header for caching. No media metadata, credentials, or local file contents are sent by this feature. As with any HTTPS request, the remote service sees connection information such as your IP address.
- **Data received:** Latest version tag, release notes, installer download URL, and, when updating, the signed manifest, detached signature, and installer.
- **Frequency:** Throttled to at most once per 45 seconds; responses are cached in memory and revalidated with ETags.
- **Security & Integrity:** Starting with version 1.9.2, in-app updates require HTTPS and an ECDSA P-256/SHA-256 signed manifest, verified locally using a public key embedded in the application. The signed version, installer name, size, and SHA-256 hash must match before the installer runs. Missing or invalid signatures and downgrades are rejected. Authenticode is optional unless an additional certificate allowlist is configured; Windows may still show an unknown-publisher or SmartScreen warning. Older clients use their existing verification for the first upgrade to this version.
- **Verification data:** The manifest and detached signature are downloaded from the same GitHub release assets as the installer and processed in memory. Signature verification does not contact a new service or upload local files, credentials, or device identifiers. Standard connection information, including your IP address and HTTP headers, is visible to GitHub and its download infrastructure.
- **Your control:** Downloading and installing an update happens **only** when you explicitly choose to. If you start an update, the matching installer (`V-Notch-Setup.exe` or `V-Notch-Setup-SelfContained.exe`) is downloaded from GitHub Releases to your temporary folder and executed.

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

**Data sent:** Track title and artist (as a search query) and standard browser-like HTTP headers. **The recipient may see the connecting IP address; track queries may reveal listening interests.** Retrieved images are held in memory for display and are not written to disk.

### 4.3 Synced Lyrics — LRCLIB and lrc mux

V-Notch tries two independent lyrics providers, in order, and stops as soon as one returns a result:

- **LRCLIB** — `https://lrclib.net/api/get?...` (exact match) and its search endpoint (fuzzy match). **Data sent:** track title, artist name, and track duration as query parameters, plus a `User-Agent` identifying V-Notch.
- **lrc mux** — `https://api.lrcmux.dev/get?...`, used as a fallback aggregator when LRCLIB has no match. **Data sent:** track title, artist name, and track duration as query parameters, plus a `User-Agent` identifying V-Notch. lrc mux is a third-party lyrics aggregation service with its own upstream sources; V-Notch does not control which upstream provider it queries internally.

**Data received (both):** Synced lyric lines, used transiently in memory for display and never written to disk. Music selections and connection IP addresses may be personal data in context and may be observable by the recipient providers.

### 4.4 YouTube Subtitles & Captions

- **Endpoint:** Public YouTube video caption endpoints.
- **Why:** To fetch timed closed captions/subtitles when playing YouTube videos and subtitles are enabled.
- **Data sent:** YouTube video ID and standard HTTP headers. No Spotify/V-Notch account credential is intended to be sent in this call; the recipient sees connection metadata such as IP address.
- **Data received:** Timed caption text, used transiently in memory for display.

### 4.5 Spotify Canvas (Experimental, Opt-In)

Canvas and its network/sign-in permission are **off by default**. Before enabling them, you must explicitly acknowledge the in-app notice about the session cookie, unofficial access methods and possible service/account restrictions. Legacy settings and imported configurations cannot supply that acknowledgement. Turning Canvas off or withdrawing its network permission clears the acknowledgement and cancels app-managed requests and sign-in. It preserves the locally encrypted cookie; use **Disconnect** to remove that cookie. Re-enabling requires another acknowledgement. This consent is a product control, not authorization from Spotify; see [Spotify's User Guidelines](https://www.spotify.com/us/legal/user-guidelines/).

When you choose **Connect Spotify**, V-Notch opens Spotify's own sign-in page in a temporary Microsoft Edge WebView2 profile. After sign-in, it reads only Spotify's `sp_dc` session cookie, clears the temporary browser profile, and stores the cookie encrypted with Windows DPAPI for the current Windows user. It is never sent to a V-Notch server or any analytics server.

While Canvas is enabled, the session is sent to Spotify (`open.spotify.com`) to obtain a short-lived access token. V-Notch sends the current track title and artist with that token to Spotify's catalog service (`api-partner.spotify.com`) to resolve the Spotify track ID. If that lookup is unavailable, it sends the title, artist, and duration to Musixmatch (`apic-desktop.musixmatch.com`) as a fallback. It then requests Canvas metadata from Spotify (`spclient.wg.spotify.com`), and streams the video from Spotify's `*.scdn.co` content delivery network. Public Spotify web-player assets (`open.spotify.com`, `open.spotifycdn.com`) may be fetched to keep the catalog query compatible; those refresh requests contain no session or track metadata. Token-generation compatibility values and initial query hashes ship locally in `Assets/MediaProtocols.json`, with embedded defaults. They are maintained through application updates without requiring users to edit configuration; the app does not contact a separate secret/configuration server. Automatic query-hash discovery from Spotify's public web-player assets remains available.

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

**Data sent:** Your IP address (to ipwho.is), or a city name (to Open-Meteo geocoding), and latitude/longitude coordinates (to Open-Meteo forecast). The service may receive connection metadata such as IP address; coordinates or music interests may be personal data depending on context.

### 4.7 Third Parties

The services above (Spotify, GitHub, Google/YouTube, Piped/Invidious instances, SoundCloud, LRCLIB, ipwho.is, and Open-Meteo) are independent third parties with their own privacy policies. When V-Notch contacts them, your IP address is visible to that service as with any normal web request. V-Notch does not control these services; legal responsibilities must be assessed under applicable law. If you prefer to avoid these lookups, you can disable artwork/lyrics/Canvas/weather features and update checks, avoid sending AI messages or refreshing balances (§4.8), or block the app's network access.

---

### 4.8 Spotlight AI (Opt-In)

For OpenAI, Gemini, Claude and DeepSeek, sending an AI message sends your prompt, recent conversation context, and selected model directly over HTTPS to the selected provider. Authentication uses your API key in request headers. These AI requests do not pass through a V-Notch backend, and automatic redirects are disabled for this HTTP client.

| Provider | API destination | Purpose |
| --- | --- | --- |
| OpenAI | `api.openai.com/v1/chat/completions` | AI responses |
| Google Gemini | `generativelanguage.googleapis.com/v1beta/models/…` | AI responses |
| Anthropic Claude | `api.anthropic.com/v1/messages` | AI responses |
| DeepSeek | `api.deepseek.com/chat/completions` | AI responses |
| DeepSeek | `api.deepseek.com/user/balance` | Balance lookup when you request a refresh |

Opening Spotlight, selecting AI as the default mode, or typing without sending does not itself submit an AI prompt. Local Search remains local. AI does not automatically attach files, clipboard contents, or screen captures; anything you paste and send becomes part of the submitted message. Restored conversations may include provider-specific Gemini response metadata used to continue that conversation.

The selected provider receives your submitted content, authentication information, IP address, and ordinary HTTP metadata. Its retention, training, international processing, and account controls depend on its current policies and your service tier or account settings. Local encryption does not prevent the provider from processing submitted content. Review [OpenAI's policies](https://openai.com/policies/), [Gemini API terms](https://ai.google.dev/gemini-api/terms), [Anthropic's commercial terms](https://www.anthropic.com/legal/commercial-terms), and [DeepSeek's privacy policy](https://platform.deepseek.com/downloads/DeepSeek%20Privacy%20Policy.html) before sending sensitive information.

The usage display holds provider-reported data in memory: latest-request token counts, rate-limit quota snapshots from supported OpenAI/Claude response headers, and DeepSeek balance results obtained on request. It is not an account-wide usage ledger or monthly budget. Unknown values are hidden; interrupted responses may have incomplete counts. These usage snapshots are not persisted to chat history. Strict Local-Only Mode and the Allow online AI privacy control block explicit AI messages and balance refreshes as well as background requests.

## 5. Local Data Storage

All persistent data created by V-Notch lives exclusively on your local device.

The canonical local data root is `%LOCALAPPDATA%\V-Notch\`. At startup, the app attempts to move older `%LOCALAPPDATA%\VNotch\` data into that root before opening its stores. Existing destination files are preserved; conflicting legacy data is retained under `legacy-migration-conflicts` in the canonical root. Files that cannot be moved remain in the legacy root for a later retry. Recovery copies have no automatic expiry.

Periodic temporary-file cleanup preserves saved chat history, quarantined `.corrupt` history, migration conflicts, settings/backups, installed translation models and resumable model downloads. Clipboard history has its separate retention rules (§5.7). The uninstaller attempts to remove both local roots and the roaming data folder; locked files or failed removal may leave data behind, so check these locations when deleting all local data.

### 5.1 Settings (`%APPDATA%\V-Notch\settings.json`)

Stores your preferences: notch size and position, visual style and Liquid Glass options, notification toggles, language, startup behavior, File Shelf contents (file paths), and feature flags. Settings may contain a YouTube API key only if you explicitly provide one and a Spotify session only if you choose Connect Spotify. These credentials, including optional AI provider keys (§5.6), are encrypted using Windows DPAPI (Data Protection API) before they are written to disk. The encrypted values are tied to the current Windows user account and are protected for the Windows user by DPAPI, subject to the security of that account and operating system. If DPAPI is unavailable, these sensitive values are not saved.

### 5.2 Spotlight Usage History (`%APPDATA%\V-Notch\spotlight-usage.json`)

Stores your recent Spotlight launches (application IDs, titles, targets, launch count, and timestamp) to provide quick access to frequent items. This store is capped at 100 entries, runs purely on your device, and is never transmitted anywhere.

### 5.3 Media Source Cache (`%APPDATA%\V-Notch\source_cache.json`)

Stores an LRU mapping (up to 500 entries) between media identity titles and resolved sources (e.g. YouTube/SoundCloud) to avoid redundant online searches for songs you play repeatedly.

### 5.4 Diagnostic Log (`vnotch-debug.log`)

Located in the application's program folder, this log records technical application events and errors to help diagnose problems. Because of what it logs, it can incidentally contain the titles/artists of tracks you played, lyrics-provider queries, and matched window titles (e.g. a browser tab title) — this is the same information already described in Sections 3 and 4, just also written locally for debugging. It is automatically rotated when it reaches about 5 MB. **V-Notch is not designed to transmit this log automatically** — it stays on your machine, is not uploaded with crash or update requests, and you may delete it at any time.

### 5.5 Optional ONNX Model

If present, the smart-crop model file (`yolox_nano.onnx`) is stored locally alongside the app and is used purely for on-device image analysis.

To remove application-managed local data, disconnect Spotify within Settings where available, close V-Notch, and remove `%APPDATA%\V-Notch\`, `%LOCALAPPDATA%\V-Notch\`, any remaining legacy `%LOCALAPPDATA%\VNotch\`, and the installation directory. The local roots include Clipboard history, AI history/recovery copies and translation models. Also check Windows temporary files, WebView/browser caches, backups, and any files submitted to GitHub; removal of these is not guaranteed by deleting the app directory. Deleting these folders permanently discards preferences and saved history.

---

### 5.6 Spotlight AI Keys and Conversation History

AI provider keys are stored in `%APPDATA%\V-Notch\settings.json` using Windows DPAPI for the current Windows user. They are excluded from settings exports, and importing settings does not replace your local AI keys. Settings also store provider/model choices, default Spotlight mode, and reveal speed. Rolling settings backups may retain older encrypted keys; removing a key from current settings does not revoke it or erase those backups. Revoke a compromised or unwanted key in the provider's dashboard.

AI history is stored separately at `%LOCALAPPDATA%\V-Notch\spotlight-chats.enc`. The encrypted file contains saved conversations, provider/model identifiers, drafts, timestamps, message contents, incomplete-response markers, and any saved Gemini response metadata. An encrypted temporary file may exist during saving. Windows DPAPI encrypts the history for the current Windows user. If history cannot be decrypted or its content is invalid, the app attempts to rename the original encrypted file to `spotlight-chats.enc.<id>.corrupt`, then allows new history to be saved. If reading or quarantine fails, it preserves the original and blocks saving until a successful retry. Saved history and quarantined files have no automatic age-based expiry.

Deleting a conversation rewrites local history when saving succeeds. Starting a new chat does not delete previous chats, and clearing Spotlight launch history does not clear AI conversations. Deleting a conversation does not remove quarantined or migration-conflict copies. Local deletion is not secure erasure and does not remove operating-system backups or provider-held records. After closing the app, you can remove the history file, associated temporary/quarantined files and legacy recovery copies to clear that local store. Include both local roots when reviewing local data under §11.

DPAPI protects data at rest, not against software running as you or a compromised account/device. Keys and chat contents must be available in memory while used. This is not end-to-end encryption against the AI provider. AI diagnostic messages are designed to record provider/status/error type rather than prompts or raw provider error bodies; credential redaction is a safeguard, not a guarantee that every diagnostic file is free of sensitive information.

### 5.7 Clipboard History (`%LOCALAPPDATA%\V-Notch\Clipboard\`)

The `items` subdirectory stores ordinary history: metadata/content JSON, images and local copies of files/folders. **Ordinary history is not encrypted by V-Notch.** Moving an item to Personal encrypts its stored content and metadata in `personal` using AES-GCM; the vault uses a six-digit passcode with a secret protected by Windows DPAPI for the current user. Automatically captured items are not placed in Personal by default. Unlocked content is available in memory, and copying/dragging Personal files creates temporary decrypted exports under `exports\personal`.

Unpinned, unarchived, non-Personal entries older than 30 days are pruned at startup and during hourly cleanup. Pinned, archived and Personal items remain until you delete them. Cleanup of old exports is attempted at startup and during periodic cleanup; files still in use can delay deletion. The root also contains `staging`, `exports` and the Personal vault configuration.

Delete individual items in the Clipboard tray; the Personal reset action deletes the Personal vault and its contents. To remove all Clipboard history, **exit V-Notch first**, then delete `%LOCALAPPDATA%\V-Notch\Clipboard\`, including its subdirectories. Removing only `%APPDATA%\V-Notch\` does not clear Clipboard history. Pause/type controls and Strict Local-Only Mode do not remove saved history. Deletion does not securely overwrite data or erase backups, exported copies or another application's clipboard history.

## 6. Data V-Notch Does NOT Collect

V-Notch does **not**:
- collect, sell, or share personal information with third parties for marketing;
- run analytics, telemetry, behavioral tracking, or device fingerprinting;
- send automated crash reports or usage statistics;
- record audio, video, or screen content;
- read, upload, or back up the contents of your files;
- access precise device GPS location;
- create user accounts, profiles, or advertising identifiers;
- automatically upload Clipboard history contents (local storage is described in §5.7);
- send local Spotlight Search queries or file index data over a network; explicitly submitted AI messages are a separate feature (§4.8).

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
| **Clipboard Format Listener** | Local Clipboard history, "Copied" confirmation animation & preview badge | Optional |
| **Windows Performance Counters** | Real-time CPU/RAM/GPU system monitor stats | Optional |

---

## 8. Privacy & Local-Only Controls

Privacy in **Settings → Privacy** controls online AI, subtitles, Spotify Canvas/sign-in, weather, and AI chat history independently, alongside artwork, lyrics, browser inspection, and logging controls. Strict Local-Only Mode overrides online permissions, cancels app-managed requests (including streamed responses), blocks manual AI, balance, and update requests, and blocks online links opened through the app. Turning offline mode off restores your individual choices. Disabling **Save AI chat history** stops loading and saving V-Notch AI history and starts an in-memory session (clearing the active in-memory conversation or draft to prevent later persistence); previously saved chats remain on disk and can be accessed or deleted again by re-enabling history. It does not control provider retention. Offline mode is an application policy, not a Windows firewall; it cannot govern other apps or guarantee that third-party runtime background traffic is absent.

V-Notch provides a dedicated **Privacy** section in Settings with granular toggles to customize your privacy posture and enforce strict offline execution:

- **Spotlight AI:** Delete saved chats from AI history and remove keys in Spotlight settings. These controls are separate from clearing launcher history. Provider records and key revocation require provider controls (§5.6).
- **Strict Local-Only Mode:** Blocks app-managed HTTP requests, including manual/automatic updates and online integrity checks, artwork/metadata, lyrics, subtitles, Canvas, weather, AI messages and balance refreshes. It blocks Spotify sign-in initialization and closes active sign-in windows. Online links opened through the app are also blocked. Existing managed requests and response-body reads receive cancellation when access is revoked. Already transmitted data cannot be recalled. Local functionality and individual online preferences remain available. This is an application control, not an operating-system firewall: it does not guarantee silence from Windows, other apps, or autonomous runtime/WebView background traffic; verify release behavior with network monitoring.
- **Automatic update checks:** Toggle periodic background checks against GitHub Releases.
- **Online album artwork lookup:** Toggle querying YouTube, SoundCloud, or Piped for album art when the active player provides no embedded cover.
- **Online synced lyrics lookup:** Toggle fetching synced lyrics from LRCLIB and lrc mux for now-playing songs.
- **Privacy indicators:** Toggle the colored hardware sensor usage dot (Mic, Camera, Screen Capture) in the compact notch.
- **Browser media URL detection:** Toggle Windows UI Automation inspection of browser address bars for media source detection.
- **Diagnostic disk logging:** Toggle writing debug messages to `vnotch-debug.log` on local disk.
- **Spotlight search launch history:** Toggle tracking application launch frecency in `spotlight-usage.json`.
- **Data clearing tools:** Single-click buttons to instantly wipe the local Spotlight launch history (`spotlight-usage.json`) and clear the diagnostic log file (`vnotch-debug.log`).

---

## 9. Security

V-Notch runs with standard user privileges and does not require administrator rights for normal operation. Administrator elevation is requested only when installing an update (to run the installer). The documented sensitive stored credentials (Spotify `sp_dc` cookie, YouTube API key, and AI provider keys) are encrypted with Windows DPAPI. In-app updates from version 1.9.2 onward require the signed-manifest verification described in Section 4.1; this does not require a paid Authenticode certificate and does not authenticate a first manual download. Because the application is fully open source, anyone may audit exactly what it does at [github.com/w0otdev/V-Notch](https://github.com/w0otdev/V-Notch).

---

## 10. Children's Privacy

V-Notch is a general-purpose desktop utility, not directed specifically to children. It has no V-Notch accounts or age verification. Local metadata and information shared with independent service providers can nevertheless be personal data. A parent/guardian should supervise optional browser, camera, and Spotify functions. Do not post a child's information to public GitHub Issues. Contact the maintainer to request review of any information you have submitted.

---

## 11. Privacy Rights and Requests

Depending on your jurisdiction and the developer's actual role, applicable law may grant rights to notice, access, rectification, erasure, objection, withdrawal of consent, portability, complaint, and other protections. This policy does not waive these rights or imply that local storage automatically fulfills them. You can use Settings to disable features and delete available local history/log files. To inspect/delete remaining application-managed files, disconnect Spotify, close the app, and review `%APPDATA%\V-Notch\`, `%LOCALAPPDATA%\V-Notch\`, any remaining legacy `%LOCALAPPDATA%\VNotch\`, the installation directory and relevant Windows temporary storage. Include quarantined chat history and migration-conflict copies (§5). Take care not to disclose private data in public Issues; use a private contact channel if provided on the repository. For information held by independent services, review their privacy notices and contact them as applicable. We will review requests concerning information under our control in accordance with applicable law; identity verification may be necessary if justified.

---

## 12. International Processing

External feature providers can receive IP addresses, requested song metadata, video IDs, city names or coordinates described in §4, and may process those outside your country. We do not represent that all such transfers are anonymous or exempt from data-protection law. The developer may also receive information users voluntarily submit on GitHub. See the third parties' own notices for their practices.

---

## 13. Policy Changes

We will update this effective date and describe material changes in the document or release notes. Where applicable law requires consent for new processing, continued use by itself will not constitute that consent. Older policy versions remain available through repository history.

---

**Revision note (October 9, 2026):** corrected Clipboard history collection, storage paths, ordinary/Personal encryption, retention, exclusion markers and data deletion guidance.

## 14. Contact

For questions or rights requests, use [GitHub Issues](https://github.com/w0otdev/V-Notch/issues); **do not post secrets, session cookies, passwords or sensitive personal information publicly**. For security-sensitive reports, seek a private contact method listed on the maintainer's GitHub profile if available. We do not promise a private contact method that has not been verified.

<!-- Publication checklist: validate network behavior, settings toggles, log paths and local deletion against the exact release binary before publication. -->

## Live translation added October 9, 2026

Live translation and its Auto option are off by default. When enabled and a model is installed, it observes the foreground application's selection through Windows UI Automation after a 300 ms pause. It excludes password controls and does not use screen capture, OCR, global Enter interception, or clipboard polling. For apps that do not expose selected text, the explicit double-Shift shortcut can use the Copy fallback described below; **Translate clipboard text** also remains available in Settings. **Replace** only acts on the selected editable control after checking that the original selection is still current; it does not submit chat messages.

Language detection and translation run locally using the separately installed model selected in Settings. Source text and results are not sent to an AI API, written to diagnostic logs, or saved as translation history. By default, up to 128 completed results are cached in memory for 30 minutes, and model resources are released after five idle minutes. Advanced settings can change these limits, disable caching, or keep the model loaded. Switching models clears the translation cache. Dismissing the popup clears its text. OS paging, crash dumps and unrelated applications' storage are outside this feature's control.

The model is not included in the installer. **Download model** makes an explicit HTTPS request to Hugging Face and its file delivery domains; those providers receive IP addresses and ordinary request metadata, never selected text. The model repository revision, byte size and SHA-256 are pinned. Local-only mode blocks downloads; offline file import remains available. Files, including resumable partial downloads, live at `%LOCALAPPDATA%\V-Notch\translation-models\<model-id>\`. **Remove model** deletes the known model and partial files.

**Copy** writes the requested translation to the Windows clipboard with exclusion markers for clipboard monitoring, history and cloud upload. V-Notch Clipboard history honors these markers. Other clipboard clients may still read or retain the text if they ignore the markers. Language preferences and enable/Auto choices are saved with the ordinary application settings.

Selection capture tries UI Automation across the event source, focused control and (for manual requests) control under the pointer, then native Windows Edit selection messages. Multiple selected ranges are combined within the 2,000-character limit; missing rectangle metadata does not discard otherwise valid text. When the user presses Shift twice and those readers cannot read the selection, the app may send a Copy shortcut to the foreground application. Recognized terminals use Ctrl+Insert rather than Ctrl+C. Only a new clipboard result owned by the source process for that request is used. The app attempts to restore the previous clipboard formats if no newer copy has occurred, and skips the fallback if those formats cannot be safely snapshotted. Temporary copies are excluded from V-Notch clipboard history. Windows history and external clipboard managers may still observe the copy made by the source application. This fallback does not run on automatic selection events and does not support Replace.
