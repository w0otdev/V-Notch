[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Path,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$CertificateThumbprint,
    [string]$TimestampUrl = 'https://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) {
    throw 'An active code-signing certificate with an accessible private key is required.'
}
$usageExtension = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }
if ('1.3.6.1.5.5.7.3.3' -notin @($usageExtension.EnhancedKeyUsages | ForEach-Object { $_.Value })) {
    throw 'Certificate does not allow code signing.'
}
$signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if ($signtool) { $signToolPath = $signtool.Source }
else {
    $candidates = @(Get-ChildItem -LiteralPath "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -File |
        Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending)
    if ($candidates.Count -eq 0) { throw 'Install the Windows SDK signing tools before signing a release.' }
    $signToolPath = $candidates[0].FullName
}
foreach ($binary in $Path) {
    $binaryPath = (Resolve-Path -LiteralPath $binary).Path
    & $signToolPath sign /sha1 $CertificateThumbprint /s My /fd SHA256 /tr $TimestampUrl /td SHA256 $binaryPath
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed: $binary" }
    & $signToolPath verify /pa /all /tw $binaryPath
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification or timestamp validation failed: $binary" }
    $signature = Get-AuthenticodeSignature -LiteralPath $binaryPath
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint -or
        $null -eq $signature.TimeStamperCertificate) { throw "Untrusted, untimestamped or unexpected signing identity: $binary" }
}
