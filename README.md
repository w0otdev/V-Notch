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
  V-Notch adds media controls, synced lyrics, system stats, privacy indicators, and a Spotlight launcher to the top of your screen. Use it as a compact pill or expand it into a floating Dynamic Island. It works with MyDockFinder.
</p>

<p align="center">
  <b>Free and open source forever.</b>
</p>

<p align="center">
  V-Notch is an independent project.<br>
  If you use it regularly, you can <a href="https://www.paypal.me/PhuocLe678"><b>support development via PayPal</b></a>.
</p>

<div align="center">

<table align="center">
  <tr>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/dynamic.gif" alt="Dynamic Island mode"><br>
      <b>Dynamic Island mode</b><br>
      <sub>A floating pill with spring animations and Liquid Glass refraction.</sub>
    </td>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/media-control.gif" alt="Media controls"><br>
      <b>Media controls</b><br>
      <sub>Control playback and volume, view album art, and seek through the current track.</sub>
    </td>
  </tr>
  <tr>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/Spotify.gif" alt="Spotify and synced lyrics"><br>
      <b>Spotify &amp; synced lyrics</b><br>
      <sub>Follow synced lyrics over changing color gradients or Canvas backgrounds.</sub>
    </td>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/volume.gif" alt="Volume and audio mixer"><br>
      <b>Volume &amp; audio mixer</b><br>
      <sub>Adjust each app’s volume or use the master slider, styled to match the album art.</sub>
    </td>
  </tr>
  <tr>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/file-shelf.gif" alt="File shelf"><br>
      <b>File shelf</b><br>
      <sub>Drop files onto the notch, then drag them into another app when you need them.</sub>
    </td>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/gesture.gif" alt="Gestures"><br>
      <b>Gestures</b><br>
      <sub>Swipe left or right to change tracks. Scroll to switch views, or swipe down to open the shelf.</sub>
    </td>
  </tr>
  <tr>
    <td align="center" valign="top" colspan="2">
      <img src="Introduction/copy.gif" alt="Clipboard notification" width="50%"><br>
      <b>Clipboard notification</b><br>
      <sub>See a notification and preview when you copy text or images.</sub>
    </td>
  </tr>
  <tr>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/liquid-glass.gif" alt="Liquid Glass optics"><br>
      <b>Liquid Glass optics</b><br>
      <sub>Glass rendered with DirectX 11, including chromatic aberration and refraction at the edges.</sub>
    </td>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/staybehind.gif" alt="Stay behind windows"><br>
      <b>Stay behind windows</b><br>
      <sub>Keep the notch on the desktop layer, below maximized apps.</sub>
    </td>
  </tr>
  <tr>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/spotlight.gif" alt="Spotlight search"><br>
      <b>Spotlight search</b><br>
      <sub>Press Alt+Space to find apps and files or calculate an expression.</sub>
    </td>
    <td align="center" valign="top" width="50%">
      <img src="Introduction/settings.gif" alt="Settings"><br>
      <b>Settings</b><br>
      <sub>Adjust the notch’s appearance and behavior in Settings.</sub>
    </td>
  </tr>
</table>

</div>

<div id="usage"></div>

## Usage

| Action                     | Result                                              |
| -------------------------- | --------------------------------------------------- |
| Hover                      | Expand the notch                                    |
| Click                      | Toggle pill / expanded view                         |
| Middle click               | Play / pause media                                  |
| Scroll down                | Switch to file shelf                                |
| Scroll up                  | Switch back to media controls                       |
| Swipe left / right         | Previous / next track                               |
| Swipe down                 | Open file shelf                                     |
| `Alt + Space`              | Open or close Spotlight                             |
| `Up` / `Down` in Spotlight | Move through results                                |
| `Tab` in Spotlight         | Switch between Search and AI                        |
| `Enter`                    | Launch selected item or send an AI message          |
| `Esc`                      | Stop an AI response, leave AI mode, or close Search |

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

### Spotlight AI

In **Settings → Spotlight**, choose a default opening mode (Search or AI). Press `Alt + Space` to open Spotlight and `Tab` to switch modes.
Opening AI mode does not send a request; sending a message transmits it and recent conversation context directly to the selected provider.
API keys are encrypted in local settings with Windows DPAPI and excluded from settings exports. AI conversations are also encrypted locally; see the [Privacy Policy](PRIVACY_POLICY.md#48-spotlight-ai-opt-in) for storage, recipients, and deletion details.

<div id="installation"></div>

## Installation

V-Notch runs on 64-bit Windows 10 build 19041 or later, or Windows 11. It requires [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) or [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). The self-contained installer includes the runtime. A dedicated GPU is recommended for Liquid Glass at 60-120 FPS.

1. Download `V-Notch-Setup.exe` from [Releases](https://github.com/rainaku/V-Notch/releases). Use `V-Notch-Setup-SelfContained.exe` to skip the .NET install.
2. Run the installer.
3. Launch V-Notch from the Start Menu or desktop shortcut.
4. Optionally enable "Start with Windows" in Settings.

<details>
<summary><strong>Build with GitHub Actions</strong></summary>

Open the Actions tab, select Release Installer, and click Run workflow. Choose `framework-dependent` (smaller, requires .NET runtime) or `self-contained` (standalone). Download the installer from Artifacts when the build finishes, or from the automated `nightly` release tag.

</details>

<div id="privacy"></div>

## Privacy

V-Notch does not collect telemetry or analytics, or track you. It makes network requests to the following services:

| Service                                              | Purpose                                                                            |
| ---------------------------------------------------- | ---------------------------------------------------------------------------------- |
| GitHub Releases API                                  | Update checks                                                                      |
| LRCLIB / lrc mux                                     | Synced lyrics                                                                      |
| YouTube / SoundCloud                                 | Public thumbnails and captions                                                     |
| Spotify Web Services                                 | Optional Canvas backgrounds; credentials encrypted with Windows DPAPI              |
| Open-Meteo / ipwho.is                                | Optional weather                                                                   |
| OpenAI / Google Gemini / Anthropic Claude / DeepSeek | Optional AI messages and conversation context; DeepSeek balance refresh on request |

Settings and several local caches are stored at `%APPDATA%\V-Notch\`. Encrypted AI chat history is stored separately at `%LOCALAPPDATA%\VNotch\spotlight-chats.enc`. Other component caches and temporary files are described in the Privacy Policy. Local Spotlight Search does not upload queries; Spotlight AI sends the content you submit to your selected provider.

> [!NOTE]
> **API Key Safety:** User-provided API credentials (OpenAI, Gemini, Claude, DeepSeek, YouTube) are encrypted locally with Windows DPAPI. V-Notch maintains no intermediary servers. Maintainers assume no liability for third-party billing, quota exhaustion, or credentials leaked from user environments or pre-release testing builds. Please configure spending caps on your provider accounts.

Privacy dots use capture reports from Windows and recording signals from Bandicam, OBS Studio, and FFmpeg. See [recording detection coverage and limitations](RECORDING_DETECTION.md).

[Privacy Policy](PRIVACY_POLICY.md) · [Tiếng Việt](PRIVACY_POLICY_VI.md) · [Terms of Service](TERMS_OF_SERVICE.md) · [Tiếng Việt](TERMS_OF_SERVICE_VI.md)

## Star history

Thank you to everyone who has supported V-Notch since the early releases. Your feedback and GitHub stars help keep the project going.

[![Star History Chart](https://api.star-history.com/svg?repos=rainaku/v-notch&type=Date)](https://star-history.com/#rainaku/v-notch&Date)

## License

V-Notch uses the Apache License 2.0. See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

<p align="center">
  Made by <a href="https://rainaku.id.vn">rainaku</a> ·
  <a href="https://v-notch.vercel.app">Website</a> ·
  <a href="https://github.com/rainaku/V-Notch">GitHub</a> ·
  <a href="https://www.facebook.com/rain.107/">Facebook</a> ·
  <a href="https://www.paypal.me/PhuocLe678">Donate</a>
</p>

