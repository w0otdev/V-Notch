param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputDirectory = '',
    [int]$Jobs = 4,
    [string]$CacheDirectory = ''
)
$ErrorActionPreference = 'Stop'
# MSBuild can pass PowerShell 7's module path to Windows PowerShell unchanged.
# Prefer this host's built-ins before autoloading Utility (Get-FileHash, etc.).
$env:PSModulePath = (Join-Path $PSHOME 'Modules') + [IO.Path]::PathSeparator + $env:PSModulePath
$Root = [IO.Path]::GetFullPath($Root)
if (!$OutputDirectory) { $OutputDirectory = Join-Path $Root 'Native/out/translation' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$cache = if ($CacheDirectory) { [IO.Path]::GetFullPath($CacheDirectory) } else { Join-Path $Root '.cache/opus-runtime' }
$inputs = @('scripts/Build-TranslationRuntime.ps1', 'Tools/TranslationRuntime/CMakeLists.txt', 'Tools/TranslationRuntime/sentencepiece.json',
    'Native/Services/Translation/LiteTranslationRuntime.cpp', 'Native/Services/Translation/LiteTranslationAbi.h',
    'Native/Services/Translation/LiteTranslationChunks.h')
$signature = ($inputs | ForEach-Object { (Get-FileHash -LiteralPath (Join-Path $Root $_) -Algorithm SHA256).Hash }) -join ':'
$stamp = Join-Path $OutputDirectory 'build.signature'
$runtimeFiles = @('VNotch.Translation.dll', 'ctranslate2.dll', 'libiomp5md.dll', 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
if ((Test-Path -LiteralPath $stamp) -and (Get-Content -LiteralPath $stamp -Raw).Trim() -eq $signature -and
    @($runtimeFiles | Where-Object { !(Test-Path -LiteralPath (Join-Path $OutputDirectory $_)) }).Count -eq 0) { return }
New-Item -ItemType Directory -Force -Path $cache,$OutputDirectory | Out-Null

# Use the published C++ DLL from the official wheel; Python and its extension are not shipped.
# The DLL's C++ ABI and headers are pinned to the same release and built with the MSVC /MD CRT.
function Get-PinnedArchive([string]$Name, [string]$Url, [string]$Hash, [string]$Destination) {
    $archive = Join-Path $cache $Name
    if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $Hash) {
        $partial = "$archive.part"
        Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $partial
        if ((Get-FileHash -LiteralPath $partial).Hash -ne $Hash) { throw "Checksum mismatch: $Name" }
        Move-Item -LiteralPath $partial -Destination $archive -Force
    }
    if (!(Test-Path -LiteralPath $Destination)) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        # A failed extraction must not become a cache hit on the next run.
        $staging = "$Destination.$([guid]::NewGuid().ToString('N'))"
        try {
            [IO.Compression.ZipFile]::ExtractToDirectory($archive, $staging)
            [IO.Directory]::Move($staging, $Destination)
        } finally {
            if (Test-Path -LiteralPath $staging) { [IO.Directory]::Delete($staging, $true) }
        }
    }
}
Get-PinnedArchive 'ctranslate2-4.9.0.whl' 'https://files.pythonhosted.org/packages/c8/c3/99c3155ac369fde882f53333558ff89aabcee910140318c2338bd1256d5b/ctranslate2-4.9.0-cp311-cp311-win_amd64.whl' 'a81306ee947ff9e8fdffd016c9131801c56472a89c50161a00dc802bd55d47b9' (Join-Path $cache 'wheel')
Get-PinnedArchive 'ctranslate2-4.9.0.zip' 'https://github.com/OpenNMT/CTranslate2/archive/refs/tags/v4.9.0.zip' '8d13eda28c31e9be21c10cd2935fe0e671ced6cacb1e638251743fabb92b3ce3' (Join-Path $cache 'ct2')
$pin = Get-Content -Raw -LiteralPath (Join-Path $Root 'Tools/TranslationRuntime/sentencepiece.json') | ConvertFrom-Json
$version = $pin.version
$spmKey = "spm-$version-$($pin.sha256.Substring(0,12))"
$sourceDirectory = Join-Path $cache $spmKey
Get-PinnedArchive "sentencepiece-$version.zip" "https://github.com/google/sentencepiece/archive/refs/tags/v$version.zip" $pin.sha256 $sourceDirectory
Write-Host "SentencePiece $version ($($pin.sha256)) from $sourceDirectory"

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ x64 build tools are required.' }
$vcvars = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
$cmake = Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
if (!(Test-Path -LiteralPath $cmake)) { $cmake = (Get-Command cmake -ErrorAction Stop).Source }
$ct2 = Join-Path $cache 'ct2/CTranslate2-4.9.0'
$spm = Join-Path $sourceDirectory "sentencepiece-$version"
$dll = Join-Path $cache 'wheel/ctranslate2/ctranslate2.dll'
$def = Join-Path $cache 'ctranslate2.def'
$lib = Join-Path $cache 'ctranslate2.lib'
$exportCommand = Join-Path $cache 'exports.cmd'
@('@echo off', ('call "{0}" >nul' -f $vcvars), ('dumpbin /exports "{0}"' -f $dll)) | Set-Content -LiteralPath $exportCommand -Encoding ascii
$exports = & cmd.exe /d /c $exportCommand
if ($LASTEXITCODE -ne 0) { throw 'Could not read the CTranslate2 export table.' }
$names = @($exports | ForEach-Object { if ($_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\S+)') { $Matches[1] } })
if ($names.Count -lt 100) { throw 'Unexpected CTranslate2 export table.' }
@('LIBRARY ctranslate2.dll','EXPORTS') + $names | Set-Content -LiteralPath $def -Encoding ascii
$importCommand = Join-Path $cache 'import.cmd'
@('@echo off', ('call "{0}" >nul' -f $vcvars), ('lib /nologo /def:"{0}" /machine:x64 /out:"{1}"' -f $def,$lib)) | Set-Content -LiteralPath $importCommand -Encoding ascii
& cmd.exe /d /c $importCommand
if ($LASTEXITCODE -ne 0) { throw 'Could not create the CTranslate2 import library.' }
$build = Join-Path $cache "build-$spmKey"
& $cmake -S (Join-Path $Root 'Tools/TranslationRuntime') -B $build -G 'Visual Studio 17 2022' -A x64 "-DCT2_SOURCE=$ct2" "-DSPM_SOURCE=$spm" "-DCT2_IMPORT_LIB=$lib" "-DRUNTIME_OUTPUT=$OutputDirectory"
if ($LASTEXITCODE -ne 0) { throw 'Translation runtime configuration failed.' }
& $cmake --build $build --config Release --target VNotchTranslation --parallel $Jobs
if ($LASTEXITCODE -ne 0) { throw 'Translation runtime compilation failed.' }
Copy-Item -LiteralPath $dll,(Join-Path $cache 'wheel/ctranslate2/libiomp5md.dll') -Destination $OutputDirectory -Force
$crt = Get-ChildItem -Path (Join-Path $vs 'VC/Redist/MSVC/*/x64/Microsoft.VC143.CRT') -Directory | Sort-Object FullName -Descending | Select-Object -First 1
if (!$crt) { throw 'The redistributable MSVC x64 CRT is missing.' }
foreach ($name in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')) {
    Copy-Item -LiteralPath (Join-Path $crt.FullName $name) -Destination $OutputDirectory -Force
}
$licenses = Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $ct2 'LICENSE') -Destination (Join-Path $licenses 'CTranslate2.txt') -Force
Copy-Item -LiteralPath (Join-Path $spm 'LICENSE') -Destination (Join-Path $licenses 'SentencePiece.txt') -Force
foreach ($dependency in 'abseil-cpp', 'protobuf', 'flatbuffers') {
    Copy-Item -LiteralPath (Join-Path $build "_deps/$dependency-src/LICENSE") -Destination (Join-Path $licenses "$dependency.txt") -Force
}
Copy-Item -LiteralPath (Join-Path $build '_deps/protobuf-src/third_party/utf8_range/LICENSE') -Destination (Join-Path $licenses 'utf8_range.txt') -Force
foreach ($dependency in 'darts_clone', 'libsais') {
    Copy-Item -LiteralPath (Join-Path $spm "third_party/$dependency/LICENSE") -Destination (Join-Path $licenses "$dependency.txt") -Force
}
Get-ChildItem -Path (Join-Path $cache 'wheel/ctranslate2-*.dist-info/licenses') -Recurse -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenses $_.Name) -Force
}
Set-Content -LiteralPath $stamp -Value $signature -Encoding ascii
