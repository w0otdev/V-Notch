[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/native')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
$binary = Join-Path $output 'VNotch.Native.dll'
if (Test-Path -LiteralPath $binary) { throw 'Native artifact already exists; use a fresh output directory.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[0-9a-f]{40}$') { throw 'Cannot identify native source commit.' }
& dotnet msbuild (Join-Path $root 'V-Notch.csproj') -t:CompileVNotchNative -p:Configuration=Release "-p:VNotchNativeArtifactPath=$binary" "-p:_VNotchNativeObjDir=$output/obj" -nologo -v:minimal
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $binary)) { throw 'Native source compilation failed.' }
@{ commit = $commit; sha256 = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash; configuration = 'Release' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'provenance.json') -Encoding utf8
