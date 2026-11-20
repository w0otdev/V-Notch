[CmdletBinding()]
param(
    [string]$TestAssemblyDirectory = (Join-Path $PSScriptRoot '../Tests/bin/Release/net10.0-windows10.0.19041.0/win-x64')
)
$ErrorActionPreference = 'Stop'

# This changes machine audio configuration. Never run it on a developer machine
# or a persistent self-hosted runner, even if Desktop.runsettings was selected.
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
    $env:RUNNER_OS -cne 'Windows' -or -not $IsWindows) {
    throw 'Desktop runner setup is restricted to disposable GitHub-hosted Windows runners.'
}
if (-not [Environment]::UserInteractive) {
    throw 'Desktop tests need an interactive Windows desktop, not a service/session-0 window station.'
}
if (-not $env:RUNNER_TEMP) { throw 'RUNNER_TEMP is required.' }

# Use the same Core Audio API as the browser integration test. A running service
# or a PnP device alone does not prove that Chrome can open a playback endpoint.
Add-Type -Path (Join-Path $TestAssemblyDirectory 'NAudio.Core.dll')
Add-Type -Path (Join-Path $TestAssemblyDirectory 'NAudio.Wasapi.dll')
function Test-DefaultRenderEndpoint {
    $enumerator = [NAudio.CoreAudioApi.MMDeviceEnumerator]::new()
    try {
        if (-not $enumerator.HasDefaultAudioEndpoint(
            [NAudio.CoreAudioApi.DataFlow]::Render, [NAudio.CoreAudioApi.Role]::Multimedia)) { return $false }
        $device = $enumerator.GetDefaultAudioEndpoint(
            [NAudio.CoreAudioApi.DataFlow]::Render, [NAudio.CoreAudioApi.Role]::Multimedia)
        try {
            if ($device.State -ne [NAudio.CoreAudioApi.DeviceState]::Active) { return $false }
            Write-Host "Desktop audio output: $($device.FriendlyName)"
            return $true
        } finally { $device.Dispose() }
    } finally { $enumerator.Dispose() }
}

foreach ($name in 'AudioEndpointBuilder', 'Audiosrv') {
    Set-Service -Name $name -StartupType Manual
    Start-Service -Name $name
}
if (Test-DefaultRenderEndpoint) { return }

# Download directly from the vendor; do not redistribute this CI-only driver in
# the application. Pin bytes as well as version, and verify Windows signatures.
# https://vb-audio.com/Cable/
$directory = Join-Path $env:RUNNER_TEMP ('vnotch-desktop-audio-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$archive = Join-Path $directory 'VBCABLE_Driver_Pack45.zip'
Invoke-WebRequest 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip' -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne
    'B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB') {
    throw 'Unexpected VB-CABLE driver archive hash.'
}
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $directory 'driver')
$installer = Join-Path $directory 'driver/VBCABLE_Setup_x64.exe'
foreach ($file in $installer, (Join-Path $directory 'driver/vbaudio_cable64_win10.cat')) {
    if ((Get-AuthenticodeSignature -LiteralPath $file).Status -ne 'Valid') {
        throw "Invalid Windows signature: $file"
    }
}
$process = Start-Process -FilePath $installer -ArgumentList '-i', '-h' -WorkingDirectory (Split-Path $installer) -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(120000)) {
        $process.Kill($true)
        throw 'VB-CABLE installation timed out.'
    }
    if ($process.ExitCode -ne 0) { throw "VB-CABLE installation failed (exit $($process.ExitCode))." }
} finally { $process.Dispose() }

for ($attempt = 0; $attempt -lt 30; $attempt++) {
    if (Test-DefaultRenderEndpoint) { return }
    Start-Sleep -Seconds 1
}
throw 'VB-CABLE installed but no active default render endpoint appeared. This runner cannot execute the browser audio gate.'
