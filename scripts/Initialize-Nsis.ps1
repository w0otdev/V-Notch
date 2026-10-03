[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CacheDirectory,
    [Parameter(Mandatory)][string]$DestinationDirectory
)

$ErrorActionPreference = 'Stop'
$version = '3.12'
# Publisher checksum: https://sourceforge.net/projects/nsis/files/NSIS%203/3.12/nsis-3.12.zip/download
$expectedHash = '56581f90db321581c5381193d796fffcf2d24b2f8fed2160a6c6a3baa67f2c4f'
$archive = Join-Path $CacheDirectory "nsis-$version.zip"
New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null

if (-not (Test-Path -LiteralPath $archive)) {
    $partial = "$archive.partial"
    try {
        $client = [System.Net.Http.HttpClient]::new()
        try {
            $client.Timeout = [TimeSpan]::FromSeconds(60)
            $url = "https://phoenixnap.dl.sourceforge.net/project/nsis/NSIS%203/$version/nsis-$version.zip"
            $bytes = $client.GetByteArrayAsync($url).GetAwaiter().GetResult()
            [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($partial), $bytes)
        } finally { $client.Dispose() }
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $expectedHash) {
            throw 'Downloaded NSIS archive does not match the publisher checksum.'
        }
        Move-Item -LiteralPath $partial -Destination $archive -Force
    } finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    }
}

# Cache only the archive; validate it on cache hits and extract fresh tools.
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'Cached NSIS archive checksum mismatch. Remove the invalid cache entry.'
}
[IO.Compression.ZipFile]::ExtractToDirectory(
    [IO.Path]::GetFullPath($archive), [IO.Path]::GetFullPath($DestinationDirectory), $true)
$compiler = Join-Path $DestinationDirectory "nsis-$version/makensis.exe"
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'NSIS compiler is missing.' }
$actualVersion = & $compiler /VERSION
if ($LASTEXITCODE -ne 0 -or $actualVersion.Trim() -ne "v$version") { throw 'Unexpected NSIS compiler version.' }
Write-Host "Verified NSIS $version portable toolchain."
[IO.Path]::GetFullPath($compiler)
