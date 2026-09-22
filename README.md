<p align="center">
  <img src="Assets/logo.png" width="100" height="100" alt="V-Notch">
</p>

<h2 align="center">V-Notch</h2>

<p align="center">
  macOS Dynamic Island and notch for Windows
</p>

<p align="center">
  <a href="https://github.com/rainaku/V-Notch/releases/latest">
    <img src="https://img.shields.io/github/v/release/rainaku/V-Notch?style=for-the-badge&color=ffffff&labelColor=eeeeee&logo=github&logoColor=111111" alt="Latest Release">
  </a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-ffffff?style=for-the-badge&labelColor=eeeeee&logo=windows&logoColor=111111" alt="Windows 10 / 11">
  <img src="https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-ffffff?style=for-the-badge&labelColor=eeeeee&logo=dotnet&logoColor=111111" alt=".NET 8 / 10">
  <a href="LICENSE">
    <img src="https://img.shields.io/github/license/rainaku/V-Notch?style=for-the-badge&color=ffffff&labelColor=eeeeee" alt="License">
  </a>
  <a href="https://github.com/rainaku/V-Notch/stargazers">
    <img src="https://img.shields.io/github/stars/rainaku/V-Notch?style=for-the-badge&color=ffffff&labelColor=eeeeee&logo=apachespark&logoColor=111111" alt="Stars">
  </a>
</p>

<p align="center">
  <a href="https://github.com/rainaku/V-Notch/releases/latest">Download</a> ·
  <a href="#usage">Usage</a> ·
  <a href="#installation">Installation</a> ·
  <a href="#privacy">Privacy</a> ·
  <a href="https://v-notch.vercel.app">Website</a>
</p>

<p align="center">
  <a href="https://github.com/rainaku/V-Notch/releases/latest">
    <img src="Assets/readme/download-framework.svg" width="240" height="64" alt="Windows · Latest release">
  </a>
</p>

<p align="center">
  <sub><a href="https://github.com/rainaku/V-Notch/releases">All releases</a></sub>
</p>

<p align="center">
  V-Notch puts a notch at the top of your screen that shows what you need: media controls, synced lyrics, system stats, privacy indicators, and a Spotlight launcher. It runs as a compact pill or expands into a floating Dynamic Island. Compatible with MyDockFinder.
</p>

<p align="center">
  <b>Free and open source forever.</b>
</p>

<p align="center">
  V-Notch is crafted independently with care and passion.<br>
  If it elevates your daily workflow, consider <a href="https://www.paypal.me/PhuocLe678"><b>supporting development via PayPal</b></a>.
</p>

---

<div align="center">

<table align="center">
  <tr>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/dynamic.gif" alt="Dynamic Island mode"><br>
      <b>Dynamic Island mode</b><br>
      <sub>Floating pill with spring physics and Liquid Glass refraction.</sub>
    </td>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/media-control.gif" alt="Media controls"><br>
      <b>Media controls</b><br>
      <sub>Playback controls, album art, volume, and real-time seek bar.</sub>
    </td>
  </tr>
  <tr>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/Spotify.gif" alt="Spotify and synced lyrics"><br>
      <b>Spotify &amp; synced lyrics</b><br>
      <sub>Real-time lyrics, dynamic color gradients, and Canvas backgrounds.</sub>
    </td>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/volume.gif" alt="Volume and audio mixer"><br>
      <b>Volume &amp; audio mixer</b><br>
      <sub>Per-app volume mixer alongside master slider matching album art.</sub>
    </td>
  </tr>
  <tr>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/file-shelf.gif" alt="File shelf"><br>
      <b>File shelf</b><br>
      <sub>Drop files onto notch to stage, drag back out to any app when ready.</sub>
    </td>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/gesture.gif" alt="Gestures"><br>
      <b>Gestures</b><br>
      <sub>Swipe to skip tracks, scroll to switch views, swipe down for shelf.</sub>
    </td>
  </tr>
  <tr>
    <td align="center" valign="top" colspan="2">
      <img src="Introduction/copy.gif" alt="Clipboard notification" width="50%"><br>
      <b>Clipboard notification</b><br>
      <sub>Instant visual feedback and content preview when copying text or images.</sub>
    </td>
  </tr>
  <tr>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/liquid-glass.gif" alt="Liquid Glass optics"><br>
      <b>Liquid Glass optics</b><br>
      <sub>DirectX 11 rendering with chromatic aberration and edge refraction.</sub>
    </td>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/staybehind.gif" alt="Stay behind windows"><br>
      <b>Stay behind windows</b><br>
      <sub>Keeps notch on desktop layer so maximized apps are never covered.</sub>
    </td>
  </tr>
  <tr>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/spotlight.gif" alt="Spotlight search"><br>
      <b>Spotlight search</b><br>
      <sub>Fast app and file launcher (Alt+Space) with math calculator.</sub>
    </td>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/settings.gif" alt="Settings"><br>
      <b>Settings</b><br>
      <sub>Config and custom the Notch however you like.</sub>
    </td>
  </tr>
</table>

</div>

---

<div id="usage"></div>

## Usage

| Action                     | Result                        |
| -------------------------- | ----------------------------- |
| Hover                      | Expands the notch             |
| Click                      | Toggle pill / expanded view   |
| Middle click               | Play / pause media            |
| Scroll down                | Switch to file shelf          |
| Scroll up                  | Switch back to media controls |
| Swipe left / right         | Previous / next track         |
| Swipe down                 | Open file shelf               |
| `Alt + Space`              | Open or close Spotlight       |
| `Up` / `Down` in Spotlight | Move through results          |
| `Enter`                    | Launch selected item          |
| `Esc`                      | Close Spotlight               |

<details>
<summary><strong>File shelf actions</strong></summary>

| Action          | Result                       |
| --------------- | ---------------------------- |
| Drag onto notch | Stage files                  |
| Lasso drag      | Select multiple staged files |
| `Ctrl + Click`  | Toggle selection             |
| Drag out        | Move to any target           |
| `Delete`        | Remove selected files        |

</details>

---

<div id="installation"></div>

## Installation

Windows 10 build 19041 or later, or Windows 11 (64-bit). Requires [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) or [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). The Self-Contained installer bundles the runtime, so you can skip that step if you use it. A dedicated GPU is recommended for Liquid Glass at 60-120 FPS.

1. Download `V-Notch-Setup.exe` from [Releases](https://github.com/rainaku/V-Notch/releases). Use `V-Notch-Setup-SelfContained.exe` to skip the .NET install.
2. Run the installer.
3. Launch V-Notch from the Start Menu or desktop shortcut.
4. Optionally enable "Start with Windows" in Settings.

<details>
<summary><strong>Building from GitHub Actions</strong></summary>

Go to the Actions tab, select Release Installer, and click Run workflow. Choose `framework-dependent` (smaller, requires .NET runtime) or `self-contained` (standalone). Download the installer from Artifacts when the build finishes, or from the automated `nightly` release tag.

</details>

---

<div id="privacy"></div>

## Privacy

No telemetry, analytics, or tracking of any kind. Network requests are limited to:

| Service               | Purpose                                                                 |
| --------------------- | ----------------------------------------------------------------------- |
| GitHub Releases API   | Update checks                                                           |
| LRCLIB / lrc mux      | Synced lyrics                                                           |
| YouTube / SoundCloud  | Public thumbnails and captions                                          |
| Spotify Web Services  | Canvas backgrounds — optional, credentials encrypted with Windows DPAPI |
| Open-Meteo / ipwho.is | Weather — optional                                                      |

All settings and caches are stored locally at `%APPDATA%\V-Notch\`.

[Privacy Policy](PRIVACY_POLICY.md) · [Tiếng Việt](PRIVACY_POLICY_VI.md) · [Terms of Service](TERMS_OF_SERVICE.md) · [Tiếng Việt](TERMS_OF_SERVICE_VI.md)

---

## Star history

A heartfelt thank you to everyone who starred and supported V-Notch from the very early days. Your early belief, feedback, and stars gave this project life and continue to inspire every single update! ⭐

[![Star History Chart](https://api.star-history.com/svg?repos=rainaku/v-notch&type=Date)](https://star-history.com/#rainaku/v-notch&Date)

---

## License

Apache License 2.0. See [LICENSE](LICENSE). Third-party notices in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

---

<p align="center">
  Made by <a href="https://rainaku.id.vn">rainaku</a> ·
  <a href="https://v-notch.vercel.app">Website</a> ·
  <a href="https://github.com/rainaku/V-Notch">GitHub</a> ·
  <a href="https://www.facebook.com/rain.107/">Facebook</a> ·
  <a href="https://www.paypal.me/PhuocLe678">Donate</a>
</p>

