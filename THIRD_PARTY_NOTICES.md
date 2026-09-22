# Third-party notices — V-Notch

**Reviewed document date:** September 22, 2026  
**Status:** Attribution inventory; not a certification of license compliance. Reconcile the bundled release with its dependency lockfile, SBOM and actual included assets before distribution.

V-Notch includes third-party components and assets listed below. Their licenses
apply to those components and assets.

## YOLO11n ONNX model

`Models/yolo11n.onnx` is YOLO11 Nano from Ultralytics, obtained from
<https://github.com/ultralytics/assets/releases> or exported with Ultralytics.
Ultralytics identifies YOLO11 model weights as licensed under AGPL-3.0 by default, with an alternative Enterprise license. Merely listing this model here does **not** satisfy AGPL obligations or make the rest of a project Apache-2.0-compatible. Obtain qualified license advice and either (a) document a valid Enterprise grant covering this exact use, (b) establish full compliance with the applicable AGPL obligations for the actual combined work and distribution, or (c) remove/replace the model and verify the replacement license **before shipping**. Do not imply Enterprise authorization without proof. Model replacement/removal path:
`Models/README.md`.

## SVG and icon assets

`Assets/*.svg` and `Services/icons/*.svg` include locally modified vector
assets and brand marks. Files identifying SVG Repo retain their SVG Repo source
comment. Brand marks remain trademarks of their respective owners; no endorsement
is implied. Verify **each file** against its original source URL, creator, precise license/version, required attribution, modification history and permission to use or distribute; replace any untraceable or noncompliant asset before shipping. Trademark attribution does not grant copyright or trademark permission.

## NuGet libraries

| Library | License | Source |
| --- | --- | --- |
| AngleSharp 1.5.0 | MIT | <https://github.com/AngleSharp/AngleSharp> |
| BlurredBackground.WPF 1.1.0 | BSD-3-Clause | <https://github.com/V4SS3UR/BlurredBackground.WPF> |
| CommunityToolkit.Mvvm 8.4.0 | MIT | <https://github.com/CommunityToolkit/dotnet> |
| FluentWpfChromes 1.0.1 | MIT | <https://github.com/vbobroff-app/FluentWpfChromes> |
| Hardcodet.NotifyIcon.Wpf 1.1.0 | CPOL-1.02 | <https://github.com/hardcodet/wpf-notifyicon> |
| MathNet.Numerics 5.0.0 | MIT | <https://numerics.mathdotnet.com/> |
| Microsoft.Extensions.DependencyInjection 8.0.1 | MIT | <https://dot.net/> |
| Microsoft.ML.OnnxRuntime 1.17.1 | MIT | <https://github.com/microsoft/onnxruntime> |
| Microsoft.Web.WebView2 1.0.4078.44 | Microsoft Software License | <https://github.com/MicrosoftEdge/WebView2Feedback> |
| NAudio 2.2.1 | MIT | <https://github.com/naudio/NAudio> |
| System.Data.OleDb 8.0.1 | MIT | <https://github.com/dotnet/runtime> |
| Vortice.Direct3D11 3.8.3 | MIT | <https://github.com/amerkoleci/Vortice.Windows> |
| Vortice.Direct3D9 3.8.3 | MIT | <https://github.com/amerkoleci/Vortice.Windows> |
| Vortice.DXGI 3.8.3 | MIT | <https://github.com/amerkoleci/Vortice.Windows> |
| WpfAnimatedGif 2.0.2 | Apache-2.0 | <https://github.com/XamlAnimatedGif/WpfAnimatedGif> |
| YoutubeExplode 6.6.0 | MIT | <https://github.com/Tyrrrz/YoutubeExplode> |

**Distribution requirements:** A source link and license label are not substitutes for including the applicable complete license/copyright texts and any mandatory NOTICE or attribution statements in each distribution where required. Audit direct **and transitive** dependencies and bundled native/runtime assets against the exact shipped binaries; version/license classifications above are inherited from the supplied inventory and are **not independently verified**. In particular, investigate CPOL-1.02 and Microsoft/WebView2 redistribution conditions. Keep corresponding permission records and an SBOM in release records.

## External APIs and retrieved content (not bundled open-source assets)

Spotify Canvas, YouTube thumbnails/captions, album artwork, lyrics, Musixmatch, LRCLIB and weather content may be subject to separate provider terms, copyright, trademark, access and caching conditions. A notice, user consent or non-affiliation disclaimer does not authorize scraping, reuse, redistribution or access through unofficial credentials. Review each implementation against the provider's current conditions, and disable functionality that lacks a defensible authorization basis.

## Required release evidence / unresolved items

- Record the checksum and provenance of `Models/yolo11n.onnx`, any commercial license grant, and an approved compatibility analysis. Until resolved, **do not include the model in a public release**.
- Generate an SBOM from the actual build (including transitive packages, models, fonts, icons, UI assets, installer, and embedded binaries); capture exact licenses and notices.
- For every SVG/icon/font/media asset, record file path, source link, author, license, required attribution, modification and replacement decision; remove unverifiable assets.
- Bundle the full required third-party license texts and NOTICE statements with source and installer as applicable; validate separately for Windows/WebView2 runtimes.
- Verify that the repository's Apache-2.0 `LICENSE` describes only material the project can legally license; third-party components retain their own licenses.

## Reference links

- Apache License 2.0: https://www.apache.org/licenses/LICENSE-2.0
- Ultralytics licensing: https://www.ultralytics.com/license
- Spotify developer terms: https://developer.spotify.com/terms

This document is a factual inventory and risk notice, not legal advice or a substitute for third-party licenses.