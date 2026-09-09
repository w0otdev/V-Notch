#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$Version,
    [string]$PrivateKeyPath,
    [string]$PublicKeyPath = (Join-Path $PSScriptRoot '../Security/update-public-key.pem')
)
$ErrorActionPreference = 'Stop'
$installer = Get-Item -LiteralPath $InstallerPath
if ($installer.Name -cnotin @('V-Notch-Setup.exe', 'V-Notch-Setup-SelfContained.exe')) {
    throw 'Unexpected installer name.'
}
$parsedVersion = $null
if (-not [Version]::TryParse($Version, [ref]$parsedVersion)) { throw 'Invalid release version.' }
if ($installer.Length -le 0 -or $installer.Length -gt 500MB) { throw 'Invalid installer size.' }
$privatePem = if ($PrivateKeyPath) { [IO.File]::ReadAllText([IO.Path]::GetFullPath($PrivateKeyPath)) } else { $env:VNOTCH_UPDATE_SIGNING_KEY_PEM }
if ([string]::IsNullOrWhiteSpace($privatePem)) { throw 'VNOTCH_UPDATE_SIGNING_KEY_PEM is required. Unsigned releases are not published.' }
$key = [Security.Cryptography.ECDsa]::Create()
$verifier = [Security.Cryptography.ECDsa]::Create()
try {
    $key.ImportFromPem($privatePem)
    $verifier.ImportFromPem([IO.File]::ReadAllText([IO.Path]::GetFullPath($PublicKeyPath)))
    if ($key.KeySize -ne 256 -or $key.ExportSubjectPublicKeyInfoPem() -cne $verifier.ExportSubjectPublicKeyInfoPem()) {
        throw 'Signing key does not match the public key embedded in this build.'
    }
    $hash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = [ordered]@{ SchemaVersion = 1; Version = $Version; InstallerName = $installer.Name; Size = $installer.Length; Sha256 = $hash }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
    $signature = $key.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    if (-not $verifier.VerifyData($bytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'Signature self-check failed.' }
    [IO.File]::WriteAllBytes($installer.FullName + '.manifest.json', $bytes)
    [IO.File]::WriteAllBytes($installer.FullName + '.manifest.sig', $signature)
    # Keep the original asset contract for clients released before signed manifests.
    [IO.File]::WriteAllText($installer.FullName + '.sha256', "$hash  $($installer.Name)")
    Write-Host "Signed update manifest for $($installer.Name) ($Version)."
} finally {
    $privatePem = $null
    $key.Dispose()
    $verifier.Dispose()
}
