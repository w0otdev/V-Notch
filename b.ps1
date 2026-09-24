# Build V-Notch Installer
# This script builds the Release version and creates the NSIS installer
#
# By default it produces a framework-dependent build (needs .NET 8 Desktop Runtime).
# Use -SelfContained to bundle the runtime so the app runs on a clean machine
# without installing .NET separately (larger installer).
param(
    [switch]$SelfContained,
    # Optional code-signing certificate. In CI, pass these from protected secrets.
    [string]$CertificatePath = '',
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '')]
    [string]$CertificatePassword = ''
)

$projectVersion = ([xml](Get-Content -Raw .\V-Notch.csproj)).Project.PropertyGroup.Version |
    Where-Object { $_ } |
    Select-Object -First 1
if ($projectVersion -match '^\d+\.\d+\.\d+$') {
    $installerVersion = "$projectVersion.0"
} elseif ($projectVersion -match '^\d+\.\d+\.\d+\.\d+$') {
    $installerVersion = $projectVersion
} else {
    throw "V-Notch.csproj Version must use major.minor.patch or major.minor.patch.revision format."
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "V-Notch Installer Build Script" -ForegroundColor Cyan
if ($SelfContained) {
    Write-Host "Mode: Self-contained (.NET runtime bundled)" -ForegroundColor Cyan
} else {
    Write-Host "Mode: Framework-dependent (needs .NET 8 Runtime)" -ForegroundColor Cyan
}
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$publishDir = "release"

# Step 1: Clean previous publish
Write-Host "[1/3] Cleaning previous publish..." -ForegroundColor Yellow
if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
    Write-Host "      Cleaned $publishDir" -ForegroundColor Green
}

# Step 2: Publish to release folder. `dotnet publish` already builds the app.
Write-Host "[2/3] Publishing to $publishDir..." -ForegroundColor Yellow
if ($SelfContained) {
    # Self-contained: bundles the .NET runtime, runs without installing .NET 8.
    dotnet publish .\V-Notch.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -o $publishDir
} else {
    # Framework-dependent single file - requires .NET 8 runtime.
    dotnet publish .\V-Notch.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -o $publishDir
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "      Publish failed!" -ForegroundColor Red
    exit 1
}

$exeVersion = (Get-Item "$publishDir\V-Notch.exe").VersionInfo.FileVersion
$appExeHash = (Get-FileHash -Algorithm SHA256 "$publishDir\V-Notch.exe").Hash.ToLowerInvariant()
Set-Content -Path "$publishDir\V-Notch.exe.sha256" -Value "$appExeHash  V-Notch.exe" -NoNewline
Write-Host "      Published successfully (v$exeVersion, SHA256: $appExeHash)" -ForegroundColor Green

# Step 2b: Publish the standalone uninstaller into the same release folder so it
# ships next to V-Notch.exe and ends up in the install directory.
Write-Host "[2b/3] Publishing uninstaller..." -ForegroundColor Yellow
dotnet publish .\Uninstall\Uninstall.csproj -c Release -r win-x64 --self-contained $SelfContained -p:Version=$projectVersion -p:AssemblyVersion=$installerVersion -p:FileVersion=$installerVersion -p:InformationalVersion=$projectVersion -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -o $publishDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "      Uninstaller publish failed!" -ForegroundColor Red
    exit 1
}
Write-Host "      Uninstaller published (uninstall.exe)" -ForegroundColor Green

if ($CertificatePath) {
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) { Write-Host "      signtool.exe not found; cannot sign binaries." -ForegroundColor Red; exit 1 }
    Write-Host "      Signing V-Notch.exe and uninstall.exe..." -ForegroundColor Yellow
    & $signtool.Source sign /fd SHA256 /f $CertificatePath /p $CertificatePassword /tr "http://timestamp.digicert.com" /td SHA256 "$publishDir\V-Notch.exe" "$publishDir\uninstall.exe"
    if ($LASTEXITCODE -ne 0) { Write-Host "      Authenticode signing of binaries failed!" -ForegroundColor Red; exit 1 }
    Write-Host "      V-Notch.exe and uninstall.exe signed successfully" -ForegroundColor Green

    $appExeHash = (Get-FileHash -Algorithm SHA256 "$publishDir\V-Notch.exe").Hash.ToLowerInvariant()
    Set-Content -Path "$publishDir\V-Notch.exe.sha256" -Value "$appExeHash  V-Notch.exe" -NoNewline
}

# Step 3: Build NSIS installer
Write-Host "[3/3] Building NSIS installer..." -ForegroundColor Yellow

# Check if NSIS is installed
$nsisPath = "C:\Program Files (x86)\NSIS\makensis.exe"
if (-not (Test-Path $nsisPath)) {
    $nsisPath = "C:\Program Files\NSIS\makensis.exe"
}

if (-not (Test-Path $nsisPath)) {
    Write-Host "      NSIS not found! Please install NSIS from https://nsis.sourceforge.io/" -ForegroundColor Red
    Write-Host "      Release files are ready in '$publishDir'" -ForegroundColor Yellow
    exit 1
}

# Create installers directory if it doesn't exist
if (-not (Test-Path "installers")) {
    New-Item -ItemType Directory -Path "installers" | Out-Null
}

# Build installer
if ($SelfContained) {
    # Tell NSIS to skip the .NET runtime check/install (runtime is bundled).
    & $nsisPath "/DSELF_CONTAINED" "/DAPP_VERSION_FULL=$installerVersion" "V-Notch-Setup.nsi"
} else {
    & $nsisPath "/DAPP_VERSION_FULL=$installerVersion" "V-Notch-Setup.nsi"
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "      NSIS build failed!" -ForegroundColor Red
    exit 1
}

# Authenticode is optional. The release workflow separately signs update manifests
# using the free ECDSA key; the updater always requires those manifests.
if ($CertificatePath) {
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) { Write-Host "      signtool.exe not found; cannot sign installer." -ForegroundColor Red; exit 1 }
    & $signtool.Source sign /fd SHA256 /f $CertificatePath /p $CertificatePassword /tr "http://timestamp.digicert.com" /td SHA256 "installers\V-Notch-Setup.exe"
    if ($LASTEXITCODE -ne 0) { Write-Host "      Authenticode signing failed!" -ForegroundColor Red; exit 1 }
    Write-Host "      Installer Authenticode signature applied" -ForegroundColor Green
} else { Write-Host "      No Authenticode certificate (optional). Release manifests must be signed separately." -ForegroundColor Yellow }

$checksum = $null
$retryCount = 0
while ($null -eq $checksum -and $retryCount -lt 10) {
    try {
        $checksum = (Get-FileHash -Algorithm SHA256 "installers\V-Notch-Setup.exe" -ErrorAction Stop).Hash.ToLowerInvariant()
    } catch {
        $retryCount++
        Start-Sleep -Milliseconds 500
    }
}
if ($null -eq $checksum) {
    Write-Host "      Failed to calculate SHA-256 due to file lock!" -ForegroundColor Red
    exit 1
}

Set-Content -Path "installers\V-Notch-Setup.exe.sha256" -Value "$checksum  V-Notch-Setup.exe" -NoNewline
Write-Host "      SHA-256 checksum created" -ForegroundColor Green

# Step 4: Automatically sign update manifest if ECDSA release key is available
$privateKeyCandidates = @(
    $env:VNOTCH_UPDATE_SIGNING_KEY_PEM_PATH,
    (Join-Path $PSScriptRoot "..\VNotchReleaseKeys\update-2026-09-private.pem")
)
$foundKey = $privateKeyCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($foundKey -or $env:VNOTCH_UPDATE_SIGNING_KEY_PEM) {
    Write-Host "      Signing update manifest..." -ForegroundColor Yellow
    $signScript = Join-Path $PSScriptRoot "scripts\Sign-UpdateManifest.ps1"
    $pwshCmd = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    $keyArg = if ($foundKey) { "-PrivateKeyPath '$foundKey'" } else { "" }
    try {
        if ($PSVersionTable.PSVersion.Major -ge 7) {
            $signParams = @{
                InstallerPath = "installers\V-Notch-Setup.exe"
                Version = $projectVersion
            }
            if ($foundKey) { $signParams["PrivateKeyPath"] = $foundKey }
            & $signScript @signParams
        } elseif ($pwshCmd) {
            & $pwshCmd.Source -NoProfile -ExecutionPolicy Bypass -Command "& '$signScript' -InstallerPath 'installers\V-Notch-Setup.exe' -Version '$projectVersion' $keyArg"
        } else {
            Write-Host "      PowerShell 7 (pwsh) not found; skipping update manifest signing." -ForegroundColor Yellow
        }
        Write-Host "      Update manifest and signature created successfully" -ForegroundColor Green
    } catch {
        Write-Host "      Warning: Could not sign update manifest: $_" -ForegroundColor Yellow
    }
}

Write-Host "      Installer created successfully" -ForegroundColor Green
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Build Complete! (v$exeVersion)" -ForegroundColor Green
Write-Host "Installer: installers\V-Notch-Setup.exe" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
