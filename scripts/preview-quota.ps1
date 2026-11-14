param(
    [ValidateSet(50, 20)][int]$Threshold = 50,
    [ValidateSet('Codex', 'Claude')][string]$Provider = 'Codex'
)

$repoRoot = Split-Path $PSScriptRoot -Parent
$appPath = Join-Path $repoRoot 'bin\Debug\net8.0-windows10.0.19041.0\win-x64\V-Notch.exe'
if (-not (Test-Path -LiteralPath $appPath)) { throw 'Build and start the updated V-Notch app first.' }
$outputDirectory = Split-Path $appPath -Parent
$assemblyPath = Join-Path $outputDirectory 'V-Notch.dll'
$nativePath = Join-Path $outputDirectory 'VNotch.Native.dll'
if (-not (Test-Path -LiteralPath $assemblyPath) -or -not (Test-Path -LiteralPath $nativePath)) {
    throw 'The updated managed and native app binaries are required for this preview.'
}
foreach ($source in @('App.xaml.cs', 'Controls\AiQuotaBanner.cs', 'Windows\MainWindow.AiUsage.cs', 'Windows\MainWindow.AiQuotaPreview.cs', 'Windows\MainWindow.AppNotification.cs')) {
    if ((Get-Item -LiteralPath (Join-Path $repoRoot $source)).LastWriteTimeUtc -gt (Get-Item -LiteralPath $assemblyPath).LastWriteTimeUtc) {
        throw 'The app binary predates the quota preview code. Build and restart V-Notch first; no dialog was opened.'
    }
}
if ((Get-Item -LiteralPath (Join-Path $repoRoot 'Native\Services\AiQuotaPreview.cpp')).LastWriteTimeUtc -gt (Get-Item -LiteralPath $nativePath).LastWriteTimeUtc) {
    throw 'The native library predates the quota preview command. Build and restart V-Notch first.'
}
$previewProcess = Start-Process -FilePath $appPath -ArgumentList @('--preview-quota', "$Threshold", $Provider) -WindowStyle Hidden -PassThru -Wait
if ($previewProcess.ExitCode -ne 0) {
    throw 'The updated V-Notch app must be running. No preview was accepted; no Windows notification was sent.'
}
Write-Host "$Provider quota preview accepted. It appears when the notch is visible and collapsed."
