[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [string]$CertificateThumbprint
)
$ErrorActionPreference = 'Stop'

function Get-ReleaseSigner([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode' -or
        -not $signature.TimeStamperCertificate) {
        throw "A trusted, timestamped Authenticode signature is required on $(Split-Path $Path -Leaf) (status $($signature.Status))."
    }
    $signature.SignerCertificate.Thumbprint
}

$installer = (Get-Item -LiteralPath $InstallerPath).FullName
$publisher = Get-ReleaseSigner $installer
if ($CertificateThumbprint -and $publisher -ne $CertificateThumbprint) {
    throw 'The installer signer does not match the configured release certificate.'
}

$programFilesRoot = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$sevenZip = Join-Path $programFilesRoot '7-Zip/7z.exe'
if (-not (Test-Path -LiteralPath $sevenZip -PathType Leaf)) {
    $command = Get-Command 7z.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { throw 'Install 7-Zip to verify the DLL in the packaged installer.' }
    $sevenZip = $command.Source
}

$tempRoot = [IO.Path]::GetTempPath()
$directory = Join-Path $tempRoot ('vnotch-signatures-' + [guid]::NewGuid().ToString('N'))
$work = New-Item -ItemType Directory -Path $directory
try {
    # Read only the exact payload paths used by V-Notch-Setup.nsi. Never run setup.
    & $sevenZip e -tNsis -y -bso0 "-o$directory" $installer '$PLUGINSDIR\payload\VNotch.Native.dll' '$PLUGINSDIR\extract-payload.exe'
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the NSIS installer for signature verification.' }
    $dll = Join-Path $directory 'VNotch.Native.dll'
    $packed = Join-Path $directory 'extract-payload.exe'
    if (Test-Path -LiteralPath $packed -PathType Leaf) {
        if (Test-Path -LiteralPath $dll) { throw 'Ambiguous installer contains both native payload layouts.' }
        & $sevenZip e -t7z -y -bso0 "-o$directory" $packed 'VNotch.Native.dll'
        if ($LASTEXITCODE -ne 0) { throw 'Could not read the 7-Zip installer payload.' }
    }
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Packaged VNotch.Native.dll is missing.' }
    if ((Get-ReleaseSigner $dll) -ne $publisher) { throw 'Packaged VNotch.Native.dll and installer signers differ.' }
    Write-Host 'Verified the installer and packaged VNotch.Native.dll publisher signatures.'
} finally {
    if ($work.FullName -eq [IO.Path]::GetFullPath($directory) -and $work.Parent.FullName -eq $tempRoot.TrimEnd('\', '/')) {
        Remove-Item -LiteralPath $work.FullName -Recurse -Force
    }
}
