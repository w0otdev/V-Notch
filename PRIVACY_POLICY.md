# Privacy Policy — V-Notch

**Effective Date:** May 29, 2026
**Application Version:** 1.7.4
**Developer:** rainaku
**Contact:** [github.com/rainaku/V-Notch/issues](https://github.com/rainaku/V-Notch/issues)

---

## 1. Introduction

V-Notch is a free, open-source desktop application for Windows that recreates a macOS-style notch / Dynamic Island experience. It displays now-playing media, battery and Bluetooth status, a file shelf, a camera preview, system volume, and other ambient information.

This Privacy Policy explains, in detail, exactly what data the application accesses, why it accesses it, where that data goes, and how long it is kept. It reflects the actual behavior of the application source code, which is publicly available for inspection at [github.com/rainaku/V-Notch](https://github.com/rainaku/V-Notch).

**Core principle:** V-Notch is local-first. It contains no analytics, no telemetry, no advertising, and requires no V-Notch account. It does not operate any server of its own. The only outbound network requests it makes are to public third-party services for a few purposes: checking for application updates, fetching album artwork / lyrics / Spotify Canvas for the media you are already playing, and — if you explicitly opt in — showing the weather. All of these are described in Section 4.

This policy uses the following terms:
- **"Local"** — data that stays on your computer and is never sent anywhere.
- **"Transient"** — data held in memory only while needed for display, then discarded; never written to disk.
- **"Opt-in"** — a feature that does nothing until you explicitly enable or trigger it.

---

## 2. Summary at a Glance

| Capability | What it accesses | Leaves your device? | Stored? |
|---|---|---|---|
| Now-playing media | Track title, artist, artwork, position, play state (Windows SMTC) | No (except artwork/lyrics lookup — see §4) | No (transient) |
| Album artwork lookup | Track title + artist sent as a search query | Yes — YouTube/Google, SoundCloud, Piped/Invidious | No (image cached in memory) |
| Synced lyrics | Track title + artist + duration sent as a query | Yes — lrclib.net | No (transient) |
| Spotify Canvas (opt-in) | Spotify web session, track title + artist | Yes — Spotify | Session encrypted locally with Windows DPAPI |
| Weather (opt-in) | Approximate IP-based location (ipwho.is) or manual city name (geocoding) | Yes — ipwho.is, Open-Meteo | No (transient) |
| Update check | Standard HTTP headers only | Yes — GitHub API | Version info cached in memory |
| Camera preview | Live camera frames | No | No (never recorded) |
| File Shelf | File paths + basic file metadata | No | Paths persisted locally (see §5) |
| System volume | Read/adjust audio endpoint level | No | No |
| Media source detection | Visible window titles; active browser URL | No | No (transient) |
| Bluetooth status | Connected device name, type, state | No | No (transient) |
| Clipboard indicator | Clipboard *change* event (not content) | No | No |
| Privacy indicators | Whether mic/camera/screen-capture is in use | No | No (transient) |
| Gestures | Mouse movement/clicks over the notch | No | No |
| Smart artwork crop | On-device image analysis (ONNX) | No | No |

---

## 3. Data Accessed on Your Device

### 3.1 Now-Playing Media (Windows Media Session)

V-Notch uses the Windows System Media Transport Controls (SMTC) API to read metadata about media currently playing on your system — for example from Spotify, the YouTube/SoundCloud web players, Apple Music, or any browser tab. The metadata includes track title, artist, album name, embedded album artwork, playback position, duration, and play/pause state.

This data is read continuously while media is playing, used to render the notch in real time, and held only in memory. It is never written to disk. The track title and artist may be sent to third-party services to look up artwork and lyrics — see Section 4.

### 3.2 Media Source Detection (Window Titles & Browser URLs)

To identify *where* media is playing (e.g. distinguishing a YouTube tab from a SoundCloud tab) and to fetch the correct artwork, V-Notch performs two kinds of local inspection:

- **Window title scanning** — It enumerates the titles of visible top-level windows and looks for known media keywords (such as "spotify", "youtube", "soundcloud", "apple music"). Only titles matching those keywords are retained, briefly, in memory.
- **Browser URL reading** — For supported browsers (Chrome, Edge, Firefox, Brave, Opera, Vivaldi), it uses the Windows UI Automation accessibility API to read the address bar and, if needed, open tabs, in order to find a media URL (a `youtube.com/watch`, `youtu.be`, or `soundcloud.com` link). Only URLs that look like media links are used.

This inspection happens entirely on your device. The titles and URLs are used transiently to drive media detection and artwork lookup, are cached only briefly in memory, and are never stored to disk or transmitted as-is. (A derived value — the track title/artist — may be sent for artwork lookup as described in Section 4.)

### 3.3 Camera (Opt-In)

V-Notch can show a live camera preview, but only when you explicitly open that feature. While active, camera frames are processed locally for on-screen display. **No frame is ever recorded, saved, photographed, or transmitted.** When you close the preview, the camera is released. When V-Notch's own camera preview is active, it suppresses its own "camera in use" privacy dot to avoid a redundant indicator.

### 3.4 File Shelf (Opt-In)

When you drag files onto the File Shelf, V-Notch records each file's path and basic file-system metadata (name, size, type) so it can display and manage the shelf. It uses a `FileSystemWatcher` on those locations to keep the shelf in sync if a file is moved or deleted. **The contents of your files are not opened, read, modified, or transmitted.** The list of file paths is saved locally so the shelf persists between sessions (see Section 5).

### 3.5 System Audio Volume

V-Notch uses the Windows Core Audio API (via NAudio) to read the current system volume and to adjust it when you use the notch's volume control. No audio is recorded or captured; only the numeric volume level of the default audio endpoint is read and set.

### 3.6 Bluetooth Device Status

V-Notch watches for Bluetooth connect/disconnect events using the Windows device enumeration API in order to show a connection notification (for example, when your headphones connect). It reads the device's display name, a category guess (headphones, speaker, keyboard, etc.), and its connection state. This information is used transiently for the on-screen notification and is not stored or transmitted.

### 3.7 Clipboard Change Indicator

V-Notch registers a Windows clipboard *format listener* so it can show a brief "Copied" confirmation animation when the clipboard changes. It reacts to the *event* that the clipboard was updated; this feature is used to trigger a visual flash and does not upload or persist clipboard data.

### 3.8 Privacy Indicators (Mic / Camera / Screen Capture)

Mirroring iOS/macOS behavior, V-Notch can display a small colored dot when your microphone, camera, or screen recording is in use by *any* application. This is a status reflection only — it indicates that a sensor is active, processes that status transiently in memory, and stores or transmits nothing.

### 3.9 Gestures & Mouse Input

To support swipe and double-tap gestures on the notch (next/previous track, open shelf, play/pause), V-Notch monitors mouse movement and clicks in the region of the notch. This input is interpreted locally to recognize gestures and is never logged or transmitted.

### 3.10 On-Device Smart Thumbnail Cropping (ONNX)

If enabled, V-Notch uses a bundled YOLO11n object-detection model running locally through ONNX Runtime to intelligently crop wide artwork (centering on a face or subject). **All image analysis runs entirely on your device. No image, model input, or detection result is sent anywhere.** This feature requires no network connection.

---

## 4. Network Connections

V-Notch has no backend server and performs no analytics or telemetry. It makes outbound requests **only** to the following public third-party services, and **only** for the purposes described. No device identifiers or tracking tokens are attached; the optional Spotify Canvas feature uses your Spotify session only as described in Section 4.4.

### 4.1 Application Update Checks — GitHub

- **Endpoint:** `https://api.github.com/repos/rainaku/V-Notch/releases/latest`
- **Why:** To detect whether a newer release of V-Notch is available.
- **Data sent:** Standard HTTP headers only, including `User-Agent: V-Notch-Updater` and a conditional `If-None-Match` (ETag) header for caching. No personal data is sent.
- **Data received:** Latest version tag, release notes, and the installer download URL.
- **Frequency:** Throttled to at most once per 45 seconds; responses are cached in memory and revalidated with ETags.
- **Your control:** Downloading and installing an update happens **only** when you explicitly choose to. If you start an update, the installer (`V-Notch-Setup.exe`) is downloaded from its GitHub release asset URL to your temporary folder and run.

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

**Data sent:** the track title and artist (as a search query) and standard browser-like HTTP headers. **No user-identifiable information is included.** Retrieved images are held in memory for display and are not written to disk.

### 4.3 Synced Lyrics — LRCLIB

- **Endpoint:** `https://lrclib.net/api/get?...`
- **Why:** To fetch time-synced lyrics for the current track, when the lyrics feature is used.
- **Data sent:** Track title, artist name, and track duration as query parameters, plus a `User-Agent` identifying V-Notch. No personal data is sent.
- **Data received:** Synced lyric lines, used transiently for display.

### 4.4 Spotify Canvas (Opt-In)

When you choose **Connect Spotify**, V-Notch opens Spotify's own sign-in page in a temporary Microsoft Edge WebView2 profile. After sign-in, it reads only Spotify's `sp_dc` session cookie, clears the temporary browser profile, and stores the cookie encrypted with Windows DPAPI for the current Windows user. It is never sent to a V-Notch or PaxSenix server.

While Canvas is enabled, the session is sent to Spotify (`open.spotify.com`) to obtain a short-lived access token. V-Notch sends the current track title and artist with that token to Spotify's catalog service (`api-partner.spotify.com`) to resolve the Spotify track ID. If that lookup is unavailable, it sends the title, artist, and duration to Musixmatch (`apic-desktop.musixmatch.com`) as a fallback. It then requests Canvas metadata from Spotify (`spclient.wg.spotify.com`), and streams the video from Spotify's `*.scdn.co` content delivery network. Public Spotify web-player assets (`open.spotify.com`, `open.spotifycdn.com`) may be fetched to keep the catalog query compatible; those refresh requests contain no session or track metadata. The rotating token secret used by Spotify's web player is downloaded from the public `xyloflake/spot-secrets-go` GitHub repository; no user data is sent with that request.

You can disconnect Spotify at any time in Settings. This removes the stored session from V-Notch. If authentication fails or no Canvas exists, V-Notch uses the normal lyrics background.

### 4.5 Weather (Opt-In)

When you enable the weather widget, V-Notch makes the following network requests **only** after you have explicitly turned the feature on. The weather widget is **off by default**; no weather-related requests are made on a fresh install until you enable it.

- **IP-based location (default):** `https://ipwho.is/` — Your approximate location (latitude, longitude, city) is resolved from your IP address. This is **not** your precise GPS location; it is a coarse geographic approximation based on your IP's registered region. Only the HTTPS endpoint is used.
- **Manual city (optional):** If you enter a city name manually, `https://geocoding-api.open-meteo.com/v1/search` is used to resolve it to coordinates. When a manual city is provided, no IP lookup is performed.
- **Weather forecast:** `https://api.open-meteo.com/v1/forecast` — The latitude/longitude (from either IP lookup or manual city entry) is sent to Open-Meteo to retrieve the current temperature, weather code, daily high/low, and timezone.
- **Frequency:** Every 15 minutes while the weather widget is active. Requests are cancelled when you turn the feature off.

If ipwho.is is unreachable, the weather widget shows "unavailable" and falls back to nothing — no HTTP-only endpoint is used.

All three endpoints are third-party services with their own privacy policies:
- [ipwho.is/privacy](https://ipwho.is/privacy)
- [open-meteo.com/privacy](https://open-meteo.com/privacy)

**Data sent:** Your IP address (to ipwho.is), or a city name (to Open-Meteo geocoding), and latitude/longitude coordinates (to Open-Meteo forecast). No other personal data is included.

### 4.6 Third Parties

The services above (Spotify, GitHub, Google/YouTube, the Piped/Invidious instances, SoundCloud, LRCLIB, ipwho.is, and Open-Meteo) are independent third parties with their own privacy policies. When V-Notch contacts them, your IP address is necessarily visible to that service, as with any normal web request. V-Notch does not control and is not responsible for how those services handle requests. If you prefer to avoid these lookups, you can disable artwork/lyrics/Canvas features, weather, and update checks, or block the app's network access.

---

## 5. Local Data Storage

All persistent data created by V-Notch lives only on your device.

### 5.1 Settings (`%APPDATA%\V-Notch\settings.json`)

 Stores your preferences: notch size and position, visual style and animation options, notification toggles, language, startup behavior, the File Shelf contents (file paths), and feature flags. Settings may contain a YouTube API key only if you explicitly provide one and a Spotify session only if you choose Connect Spotify. Both values are encrypted using Windows DPAPI (Data Protection API) before they are written to disk. The encrypted values use the current Windows user account and cannot be decrypted by another user or on another machine. If DPAPI is unavailable, these sensitive values are not saved.

### 5.2 Diagnostic Log (`vnotch-debug.log`)

Located in the application's program folder, this log records application events and errors to help diagnose problems. It is automatically rotated when it reaches about 5 MB. It is intended to contain only technical diagnostic information (and, for media debugging, may include track titles/URLs that the app is processing). **This log is never transmitted anywhere** — it stays on your machine, and you may delete it at any time.

### 5.3 Optional ONNX Model

If present, the smart-crop model file (`yolo11n.onnx`) is stored locally alongside the app and is used purely for on-device image analysis.

You can remove all stored data at any time by deleting the `%APPDATA%\V-Notch\` folder and the application directory.

---

## 6. Data V-Notch Does NOT Collect

V-Notch does **not**:
- collect, sell, or share personal information with third parties for marketing;
- run analytics, telemetry, behavioral tracking, or fingerprinting;
- send automated crash reports or usage statistics;
- record audio, video, or screen content;
- read, upload, or back up the contents of your files;
- access precise device GPS location;
- create user accounts, profiles, or advertising identifiers;
- store clipboard contents.

---

## 7. Permissions Reference

| Permission / API | Purpose | Required? |
|---|---|---|
| Media Session (SMTC) | Show now-playing media | Yes (core feature) |
| Audio Endpoint (Core Audio) | Read/control system volume | Yes (core feature) |
| Internet | Update checks, artwork & lyrics lookup, weather | Optional |
| Camera | Camera preview in the notch | Opt-in |
| File System | File Shelf drag-and-drop | Opt-in |
| UI Automation | Detect active media URL in browsers | Used for media detection |
| Bluetooth (device enumeration) | Connect/disconnect notifications | Optional |
| Clipboard listener | "Copied" confirmation animation | Optional |

---

## 8. Security

V-Notch runs with standard user privileges and does not require administrator rights for normal operation. Administrator elevation is requested only when installing an update (to run the installer). Because the application is fully open source, anyone may audit exactly what it does at [github.com/rainaku/V-Notch](https://github.com/rainaku/V-Notch).

---

## 9. Children's Privacy

V-Notch does not collect personal data from anyone, including children, and does not direct any content toward children specifically. It is suitable for all ages.

---

## 10. International Use

V-Notch processes data locally on your device. The only data that crosses a network is the limited request data described in Section 4, sent to the third-party services listed there, which may operate in various countries. No personal data is transferred or stored by the developer.

---

## 11. Changes to This Policy

This Privacy Policy may be updated as features change. Material changes will be reflected in this document, in the application changelog, and through an updated effective date and version number above. Continued use of the application after an update constitutes acceptance of the revised policy.

---

## 12. Contact

Questions, concerns, or data-related requests can be raised by opening an issue at:
[https://github.com/rainaku/V-Notch/issues](https://github.com/rainaku/V-Notch/issues)
