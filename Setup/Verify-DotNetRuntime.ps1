param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]{128}$')][string]$ExpectedSha512
)

$ErrorActionPreference = 'Stop'
try {
    $actualHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA512).Hash
    if ($actualHash -ne $ExpectedSha512) { throw 'The runtime digest does not match the pinned release.' }

    $signature = Get-AuthenticodeSignature -LiteralPath $InstallerPath
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
        throw 'The runtime does not have a valid Microsoft signature.'
    }
    exit 0
} catch {
    Write-Error 'Runtime verification failed.' -ErrorAction Continue
    exit 1
}
