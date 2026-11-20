[CmdletBinding()]
param([string]$PinPath = (Join-Path $PSScriptRoot 'TranslationRuntime/sentencepiece.json'))

$ErrorActionPreference = 'Stop'
$pin = Get-Content -Raw -LiteralPath $PinPath | ConvertFrom-Json
if ($pin.version -notmatch '^\d+\.\d+\.\d+$' -or $pin.sha256 -notmatch '^[a-f0-9]{64}$') {
    throw 'Invalid SentencePiece source pin.'
}
# OSV records upstream releases (including CVE-2026-1260) under the PyPI coordinate.
$query = @{ package = @{ ecosystem = 'PyPI'; name = 'sentencepiece' }; version = $pin.version }
$result = Invoke-RestMethod -Uri 'https://api.osv.dev/v1/query' -Method Post -ContentType 'application/json' -Body ($query | ConvertTo-Json) -TimeoutSec 60
if ($result -isnot [pscustomobject]) { throw 'Invalid OSV response for SentencePiece.' }
foreach ($vulnerability in $result.vulns) {
    throw "SentencePiece $($pin.version): https://osv.dev/vulnerability/$($vulnerability.id) ($($vulnerability.aliases -join ', '))"
}
Write-Host "OSV reported no known advisories for SentencePiece $($pin.version)."
