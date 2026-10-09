# Third-party notices — V-Notch

**Reviewed document date:** October 9, 2026
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

| Library | License | Source |
| --- | --- | --- |
| CommunityToolkit.Mvvm 8.4.0 | MIT | <https://github.com/CommunityToolkit/dotnet> |
| Hardcodet.NotifyIcon.Wpf 1.1.0 | CPOL-1.02 | <https://github.com/hardcodet/wpf-notifyicon> |
| LanguageDetection.Ai 1.1.0 | Apache-2.0 (package license URL) | <https://www.nuget.org/packages/LanguageDetection.Ai/1.1.0> |
| LLamaSharp 0.27.0 | MIT | <https://github.com/SciSharp/LLamaSharp/tree/v0.27.0> |
| LLamaSharp.Backend.Cpu / LLamaSharp.Backend.Vulkan.Windows 0.27.0 | MIT core; included native dependency notices in `Assets/Translation/Licenses/` | <https://github.com/SciSharp/LLamaSharp/tree/v0.27.0> |
| Markdig 0.41.3 | BSD-2-Clause | <https://github.com/xoofx/markdig> |
| AngleSharp 1.8.2 | MIT | <https://github.com/AngleSharp/AngleSharp> |
| JsonExtensions 1.2.0 (transitive via YoutubeExplode) | MIT | <https://github.com/Tyrrrz/JsonExtensions> |
| Microsoft.Extensions.DependencyInjection 8.0.1 | MIT | <https://dot.net/> |
| Microsoft.ML.OnnxRuntime 1.17.1 | MIT | <https://github.com/microsoft/onnxruntime> |
| Microsoft.Web.WebView2 1.0.4078.44 | Microsoft Software License | <https://github.com/MicrosoftEdge/WebView2Feedback> |
| NAudio 2.2.1 | MIT | <https://github.com/naudio/NAudio> |
| System.Data.OleDb 8.0.1 | MIT | <https://github.com/dotnet/runtime> |
| Vortice.Direct3D11 3.8.3 | MIT | <https://github.com/amerkoleci/Vortice.Windows> |
| Vortice.Direct3D9 3.8.3 | MIT | <https://github.com/amerkoleci/Vortice.Windows> |
| Vortice.DXGI 3.8.3 | MIT | <https://github.com/amerkoleci/Vortice.Windows> |
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
- Replace the CPOL-1.02 tray component before claiming an Apache-compatible dependency set; see [the assessment and replacement plan](NOTIFYICON_REPLACEMENT_PLAN.md). The restored 1.1.0 package and its source commit contain CPOL, even though upstream master now contains MIT.
- Optional offline translation uses the pinned Qwen model described below, not the earlier M2M100/MADLAD research candidates. Tokenizers.DotNet is restricted to the benchmark project and is not a production dependency. Do not distribute research model assets as part of the app.

## Reference links

- Apache License 2.0: https://www.apache.org/licenses/LICENSE-2.0
- Ultralytics licensing: https://www.ultralytics.com/license
- Spotify developer terms: https://developer.spotify.com/terms

This document is a factual inventory and risk notice, not legal advice or a substitute for third-party licenses.

## Complete licenses and copyright notices for added components

### Offline translation runtime and optional model

The default model is [Qwen3-4B-Instruct-2507](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507), quantized as Q4_K_M by [Unsloth](https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF) (Apache-2.0). The download is pinned to revision `a06e946bb6b655725eafa393f4a9745d460374c9`; exact filename, size, SHA-256, original model revision and provenance limits are in `Assets/Translation/model-provenance.json` and `Services/Translation/TranslationModelProfile.cs`. Model weights are downloaded or imported only on the user's explicit action and are excluded from the installer. The unchanged upstream Apache-2.0 text is distributed in `Assets/Translation/Licenses/Qwen-Apache-2.0.txt`.

LLamaSharp 0.27.0 and its CPU/Windows Vulkan backends are included as runtime dependencies. Full collected license/copyright notices are distributed in `Assets/Translation/Licenses/LLamaSharp-Native-CPU.txt`, `LLamaSharp-Native-Vulkan.txt` and `LLamaSharp-Managed-Dependencies.txt`. These include the SciSharp/llama.cpp/ggml MIT notices and applicable bundled vendor texts. The package-declared llama.cpp revision differs from the tag's submodule reference; this is documented in the collected notices. The upstream backend packages do not provide an exact binary build SBOM; conversion and binary builds were not independently reproduced.

LanguageDetection.Ai 1.1.0 (Kasper Rune Sogaard, Pēteris Ņikiforovs, Nakatani Shuyo) provides local language identification under Apache-2.0. Original Java library author: Nakatani Shuyo; the .NET project was forked from TechnikEmpire/language-detection. Its unchanged upstream license is distributed in `Assets/Translation/Licenses/LanguageDetection-Apache-2.0.txt`; package source reference `e2d35ba3502081119693f2155486825f7bd8a727`.

The translation runtime depends on the installed Microsoft Visual C++ v14 x64 runtime (including OpenMP) and, for GPU acceleration, the GPU driver's Vulkan loader. Those system runtimes are not copied from a developer machine or redistributed by this feature. Install Microsoft's redistributable using its official installer; see [Microsoft's supported runtime downloads](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170). The app reports a missing runtime without installing anything automatically.

### Markdig 0.41.3 — BSD-2-Clause

Markdown parsing. Copyright (c) 2018-2019, Alexandre Mutel.

Complete upstream license: <https://raw.githubusercontent.com/xoofx/markdig/7ff8db9016593b71f9ae17d9b2b053fbd54e9cdf/license.txt>

~~~text
Copyright (c) 2018-2019, Alexandre Mutel
All rights reserved.

Redistribution and use in source and binary forms, with or without modification
, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
~~~

### AngleSharp 1.8.2 — MIT

HTML parsing. Copyright (c) 2013 - 2026 AngleSharp.

Complete upstream license: <https://raw.githubusercontent.com/AngleSharp/AngleSharp/35b26db83557a6a74ce286907833e3b17806d9eb/LICENSE>

~~~text
The MIT License (MIT)

Copyright (c) 2013 - 2026 AngleSharp

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
~~~

### JsonExtensions 1.2.0 — MIT

Transitive dependency of YoutubeExplode. Copyright (c) 2020-2021 Alexey Golub (Tyrrrz; now known as Oleksii Holub).

Complete upstream license: <https://raw.githubusercontent.com/Tyrrrz/JsonExtensions/a7f72f1a9673258411a35d5aa40453f3cc917cf4/License.txt>

~~~text
MIT License

Copyright (c) 2020-2021 Alexey Golub

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
~~~

### OverShifted/LiquidGlass — MIT

Copyright (c) 2026 Sepehr Kalanaki (OverShifted). The adapted refraction formulas and noise in Shaders/LiquidGlassRefraction.hlsl, and its compiled .ps output, are based on this project. V-Notch contains a modified HLSL adaptation for its WPF rendering pipeline. License reference commit: 3797fa541c1f026c521a75885ee271f03bdf9f0f; the original adaptation commit is not recorded.

Complete upstream license: <https://raw.githubusercontent.com/OverShifted/LiquidGlass/3797fa541c1f026c521a75885ee271f03bdf9f0f/LICENSE>

~~~text
MIT License

Copyright (c) 2026 Sepehr Kalanaki

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
~~~

### m2m100_418M translation model — upstream MIT

Research benchmark only; excluded from the production translation feature and installer. Original model: Facebook AI / Meta, facebook/m2m100_418M (https://huggingface.co/facebook/m2m100_418M). Copyright (c) Facebook, Inc. and its affiliates. The upstream model card declares MIT; the full Fairseq MIT text is reproduced below. ONNX/int8 conversion and tokenizer assets: Xenova/m2m100_418M at revision 9c374f0b7aca709787cea97b047bfbbd1559d177 (https://huggingface.co/Xenova/m2m100_418M/tree/9c374f0b7aca709787cea97b047bfbbd1559d177). Research hashes are recorded in Tools/TranslationBench/Research/TranslationResearchProfiles.cs. The conversion card identifies the base model but does not supply a separate conversion license; this notice does not assert additional conversion rights. Fairseq license reference commit: 3d262bb25690e4eb2e7d3c1309b1e9c406ca4b99.

Complete upstream license: <https://raw.githubusercontent.com/facebookresearch/fairseq/3d262bb25690e4eb2e7d3c1309b1e9c406ca4b99/LICENSE>

~~~text
MIT License

Copyright (c) Facebook, Inc. and its affiliates.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
~~~

### Additional optional translation models

The selectable catalog also includes TranslateGemma 4B/12B (Gemma terms), Hunyuan-MT 7B (Tencent Hunyuan terms), Qwen2.5 14B, Qwen3 14B, Qwen3.5 27B and Gemma 4 26B A4B/31B (Apache-2.0). Weights are not bundled. Community GGUF conversions are identified separately from the original publishers. See [the model catalog](TRANSLATION_MODELS.md) for sources and capacity estimates; pinned revisions, asset sizes and SHA-256 values are in `Services/Translation/TranslationModelCatalog.cs`. The source button in Settings opens the selected repository and its license information before download.
