[CmdletBinding()]
param([string]$RootDirectory = (Split-Path $PSScriptRoot -Parent))

$ErrorActionPreference = 'Stop'
$modelDir = Join-Path $RootDirectory 'Models'
$manifest = Get-Content -LiteralPath (Join-Path $modelDir 'model-provenance.json') -Raw | ConvertFrom-Json
if ($manifest.file -ne 'yolox_nano.onnx' -or $manifest.license -ne 'Apache-2.0' -or
    $manifest.source -ne 'https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_nano.onnx') {
    throw 'Unexpected model provenance. Review distribution permissions before changing the allowlist.'
}
$scanRoot = $RootDirectory
if ((Resolve-Path -LiteralPath $RootDirectory).Path -eq (Split-Path $PSScriptRoot -Parent)) { $scanRoot = $modelDir }
$models = @(Get-ChildItem -LiteralPath $scanRoot -Recurse -File -Filter '*.onnx')
if ($models.Count -ne 1 -or $models[0].Name -ne $manifest.file) { throw 'Only the reviewed YOLOX-Nano model may be bundled.' }
$expectedHash = 'c789161ed43c8269fcd4e67c67eeeb4e80c622da2eb296a20bc6007bd18a0b7d'
if ($manifest.sha256 -ne $expectedHash -or
    (Get-FileHash -LiteralPath $models[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash -or
    $models[0].Length -ne 3659407) { throw 'Model checksum or length does not match the reviewed upstream asset.' }
$license = Get-Content -LiteralPath (Join-Path $modelDir 'YOLOX-LICENSE.txt') -Raw
$licenseBytes = [Text.Encoding]::UTF8.GetBytes($license.Replace("`r`n", "`n").Trim())
$licenseHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($licenseBytes)).ToLowerInvariant()
if ($licenseHash -ne '7994a4e6c6c7d75ff4a521812be618e7faa46ef4db0c445bdea100091d3dae44') {
    throw 'The complete upstream YOLOX license and copyright notice must accompany the model.'
}
Write-Host 'Reviewed YOLOX-Nano asset, provenance, checksum and license verified.'
