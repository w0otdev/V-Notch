# Third-party notices — V-Notch

**Reviewed document date:** October 3, 2026
**Status:** Attribution inventory; not a certification of license compliance. Reconcile the bundled release with its dependency lockfile, SBOM and actual included assets before distribution.

V-Notch includes third-party components and assets listed below. Their licenses
apply to those components and assets.

## YOLOX-Nano ONNX model

`Models/yolox_nano.onnx` is the unmodified YOLOX-Nano asset published by
Megvii-BaseDetection in [YOLOX release 0.1.1rc0](https://github.com/Megvii-BaseDetection/YOLOX/releases/tag/0.1.1rc0).
YOLOX is published under [Apache-2.0](https://github.com/Megvii-BaseDetection/YOLOX/blob/6ddff4824372906469a7fae2dc3206c7aa4bbaee/LICENSE),
Copyright (c) 2021–2022 Megvii Inc. The complete upstream license/copyright is
distributed as `Models/YOLOX-LICENSE.txt`. `Models/model-provenance.json` records
the upstream download, reference commit, exact SHA-256 and inference contract.
CI and installer builds reject unreviewed or modified model bytes.

The former Ultralytics `yolo11n.onnx` has been removed from the current tree and
installer payload. Earlier Git history/releases remain a separate review item.
Removal does not retroactively resolve obligations for previous distributions.
See `Models/README.md` for the replacement's preprocessing and output contract.

## SVG and icon assets

`Assets/*.svg` and `Services/icons/*.svg` include locally modified vector
assets and brand marks. Files identifying SVG Repo retain their SVG Repo source
comment. Brand marks remain trademarks of their respective owners; no endorsement
is implied. Verify **each file** against its original source URL, creator, precise license/version, required attribution, modification history and permission to use or distribute; replace any untraceable or noncompliant asset before shipping. Trademark attribution does not grant copyright or trademark permission.

## NuGet libraries

GitHub Copilot support uses `GitHub.Copilot.SDK` 1.0.16 (MIT, Copyright GitHub, Inc.) and the SDK-pinned GitHub Copilot CLI runtime 1.0.90. License texts are distributed in `Assets/Licenses/GitHub-Copilot-SDK.txt` and `Assets/Licenses/GitHub-Copilot-CLI.txt`. The runtime is an unmodified third-party component under the GitHub Copilot CLI License, not V-Notch's Apache-2.0 license. GitHub Copilot service access requires separate authorization and applicable service terms. GitHub and Copilot names identify the integration and do not imply endorsement.

| Library | License | Source |
| --- | --- | --- |
| CommunityToolkit.Mvvm 8.4.0 | MIT | <https://github.com/CommunityToolkit/dotnet> |
| Hardcodet.NotifyIcon.Wpf 1.1.0 | CPOL-1.02 | <https://github.com/hardcodet/wpf-notifyicon> |
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

Canvas is experimental, off by default and requires explicit in-app acknowledgement before sign-in or network access. This control does not resolve the authorization question. YoutubeExplode's MIT license covers the library, not permission to access YouTube or use caption content. See [Spotify's User Guidelines](https://www.spotify.com/us/legal/user-guidelines/), [YouTube's Terms](https://www.youtube.com/static?template=terms) and the [Fair Use Index](https://www.copyright.gov/fair-use/); a fair-use notice is not a blanket exemption.

## Required release evidence / unresolved items

- Keep the YOLOX model, complete upstream license and provenance together in each distribution; reassess permissions and inference contracts before any replacement. Review previously distributed YOLO11 assets separately.
- Generate an SBOM from the actual build (including transitive packages, models, fonts, icons, UI assets, installer, and embedded binaries); capture exact licenses and notices.
- For every SVG/icon/font/media asset, record file path, source link, author, license, required attribution, modification and replacement decision; remove unverifiable assets.
- Bundle the full required third-party license texts and NOTICE statements with source and installer as applicable; validate separately for Windows/WebView2 runtimes.
- Verify that the repository's Apache-2.0 `LICENSE` describes only material the project can legally license; third-party components retain their own licenses.

## Reference links

- Apache License 2.0: https://www.apache.org/licenses/LICENSE-2.0
- Ultralytics licensing: https://www.ultralytics.com/license
- Spotify developer terms: https://developer.spotify.com/terms

This document is a factual inventory and risk notice, not legal advice or a substitute for third-party licenses.
