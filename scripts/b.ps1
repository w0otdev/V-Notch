# Build V-Notch Installer
# This script builds the Release version and creates the NSIS installer
#
# By default it produces a framework-dependent build (needs .NET 8 Desktop Runtime).
# Use -SelfContained to bundle the runtime so the app runs on a clean machine
# without installing .NET separately (larger installer).
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'CertificatePassword')]
param(
    [switch]$SelfContained,
    [string]$NsisPath = '',
    [switch]$LockedRestore,
    [string]$ReleaseVersion = '',
    [string]$WindowsVersion = '',
    [string]$InformationalVersion = '',
    # Optional code-signing certificate. In CI, pass these from protected secrets.
    [string]$CertificatePath = '',
    [string]$CertificatePassword = '',
    [string]$CertificateThumbprint = '',
    [switch]$RequireAuthenticode
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ge 7) {
    $PSNativeCommandUseErrorActionPreference = $false
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Invoke-CapturedNative([scriptblock]$Command) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Command 2>&1
    } finally {
        $ErrorActionPreference = $prev
    }
}

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$repoRoot = Split-Path $scriptDir -Parent

# Capture the conditional output as an array: a single string splats into
# individual characters when passed to a native command such as dotnet.
$restoreArguments = @(if ($LockedRestore) { '-p:RestoreLockedMode=true' })
if ($RequireAuthenticode -and -not $CertificateThumbprint) {
    throw 'A trusted certificate thumbprint is required for a public release.'
}

# Runtime Unicode Box-Drawing & Status Glyphs (7-bit ASCII source safe for PS 5.1 & PS 7)
$gTopLeft = [char]0x256D
$gBotLeft = [char]0x2570
$gVert    = [char]0x2502
$gBranch  = [char]0x251C
$gHoriz   = [char]0x2500
$gDiamond = [char]0x25C6
$gCheck   = [char]0x2714
$gCross   = [char]0x2716
$gWarn    = [char]0x25B2
$gArrow   = [char]0x25B8
$lineRule = ([string]$gHoriz) * 56

function Write-TreeStep([string]$Tag, [string]$Title) {
    Write-Host "  $gBranch$gHoriz " -ForegroundColor DarkCyan -NoNewline
    Write-Host "[$Tag] " -ForegroundColor Cyan -NoNewline
    Write-Host $Title -ForegroundColor White
}

function Write-TreeItem([string]$Message, [string]$State = "Info") {
    switch ($State) {
        "Info"    { $sym = $gArrow; $sCol = "DarkCyan"; $tCol = "Gray" }
        "Success" { $sym = $gCheck; $sCol = "Green";    $tCol = "Gray" }
        "Warn"    { $sym = $gWarn;  $sCol = "Yellow";   $tCol = "Yellow" }
        "Error"   { $sym = $gCross; $sCol = "Red";      $tCol = "Red" }
    }
    Write-Host "  $gVert     " -ForegroundColor DarkCyan -NoNewline
    Write-Host "$sym " -ForegroundColor $sCol -NoNewline
    Write-Host $Message -ForegroundColor $tCol
}

function Write-TreeSpacer {
    Write-Host "  $gVert" -ForegroundColor DarkCyan
}

$totalSw = [System.Diagnostics.Stopwatch]::StartNew()

Push-Location $repoRoot
try {

$projectPath = Join-Path $repoRoot 'V-Notch.csproj'
$projectVersion = ([xml](Get-Content -Raw $projectPath)).Project.PropertyGroup.Version |
    Where-Object { $_ } |
    Select-Object -First 1
if ($projectVersion -match '^\d+\.\d+\.\d+$') {
    $installerVersion = "$projectVersion.0"
} elseif ($projectVersion -match '^\d+\.\d+\.\d+\.\d+$') {
    $installerVersion = $projectVersion
} else {
    throw "V-Notch.csproj Version must use major.minor.patch or major.minor.patch.revision format."
}
$assemblyVersion = $installerVersion
if (-not $ReleaseVersion) { $ReleaseVersion = $projectVersion }
if ($ReleaseVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw 'Invalid release version.'
}
if ($WindowsVersion) { $installerVersion = $WindowsVersion }
if ($installerVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' -or
    @($installerVersion.Split('.') | Where-Object { [long]$_ -gt 65534 }).Count -gt 0) {
    throw 'Installer version must have four numeric components within Windows version limits.'
}
if (-not $InformationalVersion) { $InformationalVersion = $ReleaseVersion }
if ($InformationalVersion -cne $ReleaseVersion -and
    ($InformationalVersion -cnotmatch '^[0-9A-Za-z.+-]+$' -or -not $InformationalVersion.StartsWith("$ReleaseVersion+", [StringComparison]::Ordinal))) {
    throw 'Informational version must match the release version and optional build metadata.'
}
$versionArguments = @(
    "-p:Version=$ReleaseVersion", "-p:AssemblyVersion=$assemblyVersion", "-p:FileVersion=$installerVersion",
    "-p:InformationalVersion=$InformationalVersion", "-p:IncludeSourceRevisionInInformationalVersion=false",
    "-p:PackageManifestVersion=$installerVersion"
)

$modeText  = if ($SelfContained) { "Self-Contained (win-x64)" } else { "Framework-Dependent (.NET 8)" }
$modeColor = if ($SelfContained) { "Magenta" } else { "Green" }

Write-Host ""
Write-Host "  $gTopLeft$lineRule" -ForegroundColor DarkCyan
Write-Host "  $gVert  " -ForegroundColor DarkCyan -NoNewline
Write-Host "$gDiamond V-NOTCH INSTALLER BUILDER " -ForegroundColor Cyan -NoNewline
Write-Host "v$ReleaseVersion" -ForegroundColor DarkGray -NoNewline
Write-Host "  $gVert  " -ForegroundColor DarkGray -NoNewline
Write-Host $modeText -ForegroundColor $modeColor
Write-Host "  $gVert" -ForegroundColor DarkCyan

$publishDir = "release"

# Step 1: Clean previous publish
Write-TreeStep "1/4" "Prepare Release Directory"
if (Test-Path $publishDir) {
    $resolvedPublish = (Resolve-Path -LiteralPath $publishDir).Path
    $expectedPublish = [IO.Path]::GetFullPath((Join-Path $repoRoot 'release'))
    if ($resolvedPublish -ne $expectedPublish) { throw 'Publish cleanup must stay inside this repository release directory.' }
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
    Write-TreeItem "Cleaned previous '$publishDir' directory." -State "Success"
} else {
    Write-TreeItem "No previous '$publishDir' directory found." -State "Info"
}

# Step 2: Publish to release folder. `dotnet publish` already builds the app.
Write-TreeSpacer
Write-TreeStep "2/4" "Publish V-Notch & Uninstaller"
$pubSw = [System.Diagnostics.Stopwatch]::StartNew()
if ($SelfContained) {
    # Self-contained: bundles the .NET runtime, runs without installing .NET 8.
    $pubOut = Invoke-CapturedNative { & dotnet publish .\V-Notch.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=none -p:DebugSymbols=false -nologo -v:q -o $publishDir @restoreArguments @versionArguments }
} else {
    # Framework-dependent single file - requires .NET 8 runtime.
    $pubOut = Invoke-CapturedNative { & dotnet publish .\V-Notch.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=none -p:DebugSymbols=false -nologo -v:q -o $publishDir @restoreArguments @versionArguments }
}
if ($LASTEXITCODE -ne 0) {
    foreach ($l in @($pubOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) {
        Write-TreeItem $l.Trim() -State "Error"
    }
    Write-TreeItem "Application publish failed!" -State "Error"
    Write-Host "  $gBotLeft$lineRule`n" -ForegroundColor DarkCyan
    exit 1
}

$publishedVersion = (Get-Item "$publishDir\V-Notch.exe").VersionInfo
$exeVersion = $publishedVersion.FileVersion
if ($exeVersion -ne $installerVersion -or $publishedVersion.ProductVersion -cne $InformationalVersion) {
    throw 'Published application version does not match the installer release identity.'
}
if (-not (Test-Path -LiteralPath "$publishDir\VNotch.Native.dll")) {
    throw 'Published VNotch.Native.dll is missing from the release output directory.'
}
$appExeHash = (Get-FileHash -Algorithm SHA256 "$publishDir\V-Notch.exe").Hash.ToLowerInvariant()
Set-Content -Path "$publishDir\V-Notch.exe.sha256" -Value "$appExeHash  V-Notch.exe"
Write-TreeItem ("Published V-Notch.exe & VNotch.Native.dll (v$exeVersion) in {0:N1}s." -f $pubSw.Elapsed.TotalSeconds) -State "Success"

# Step 2b: Publish the standalone uninstaller into the same release folder so it
# ships next to V-Notch.exe and ends up in the install directory.
$uninstOut = Invoke-CapturedNative { & dotnet publish .\Uninstall\Uninstall.csproj -c Release -r win-x64 --self-contained $SelfContained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=none -p:DebugSymbols=false -nologo -v:q -o $publishDir @restoreArguments @versionArguments }
if ($LASTEXITCODE -ne 0) {
    foreach ($l in @($uninstOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) {
        Write-TreeItem $l.Trim() -State "Error"
    }
    Write-TreeItem "Uninstaller publish failed!" -State "Error"
    Write-Host "  $gBotLeft$lineRule`n" -ForegroundColor DarkCyan
    exit 1
}
Write-TreeItem "Published standalone uninstaller (uninstall.exe)." -State "Success"

$modelCheckOut = & ./Tools/Assert-ModelAssets.ps1 -RootDirectory $publishDir *>&1
Write-TreeItem "Verified bundled ONNX model provenance & license." -State "Success"

if ($CertificateThumbprint) {
    & (Join-Path $scriptDir 'Sign-ReleaseBinary.ps1') -Path "$publishDir\V-Notch.exe", "$publishDir\VNotch.Native.dll", "$publishDir\uninstall.exe" -CertificateThumbprint $CertificateThumbprint
    $appExeHash = (Get-FileHash -Algorithm SHA256 "$publishDir\V-Notch.exe").Hash.ToLowerInvariant()
    Set-Content -Path "$publishDir\V-Notch.exe.sha256" -Value "$appExeHash  V-Notch.exe"
    Write-TreeItem "Signed V-Notch.exe, VNotch.Native.dll and uninstall.exe (thumbprint)." -State "Success"
}

if ($CertificatePath -and -not $CertificateThumbprint) {
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) { Write-TreeItem "signtool.exe not found; cannot sign binaries." -State "Error"; exit 1 }
    Write-TreeItem "Signing V-Notch.exe, VNotch.Native.dll and uninstall.exe..." -State "Info"
    & $signtool.Source sign /fd SHA256 /f $CertificatePath /p $CertificatePassword /tr "http://timestamp.digicert.com" /td SHA256 "$publishDir\V-Notch.exe" "$publishDir\VNotch.Native.dll" "$publishDir\uninstall.exe"
    if ($LASTEXITCODE -ne 0) { Write-TreeItem "Authenticode signing of binaries failed!" -State "Error"; exit 1 }
    Write-TreeItem "V-Notch.exe, VNotch.Native.dll and uninstall.exe signed successfully." -State "Success"

    $appExeHash = (Get-FileHash -Algorithm SHA256 "$publishDir\V-Notch.exe").Hash.ToLowerInvariant()
    Set-Content -Path "$publishDir\V-Notch.exe.sha256" -Value "$appExeHash  V-Notch.exe"
}

# Step 3: Build NSIS installer
Write-TreeSpacer
Write-TreeStep "3/4" "Package NSIS Installer"

# Accept the verified portable compiler from CI, retaining installed NSIS for developers.
if (-not $NsisPath) {
    $NsisPath = "C:\Program Files (x86)\NSIS\makensis.exe"
    if (-not (Test-Path -LiteralPath $NsisPath)) {
        $NsisPath = "C:\Program Files\NSIS\makensis.exe"
    }
}

if (-not (Test-Path $NsisPath)) {
    Write-TreeItem "NSIS not found! Install NSIS from https://nsis.sourceforge.io/" -State "Error"
    Write-TreeItem "Release files are ready in '$publishDir'." -State "Warn"
    Write-Host "  $gBotLeft$lineRule`n" -ForegroundColor DarkCyan
    exit 1
}

if (-not (Test-Path "installers")) {
    New-Item -ItemType Directory -Path "installers" | Out-Null
}

# Read the UTF-8 source explicitly; ANSI decoding turns the copyright sign into mojibake.
if ($SelfContained) {
    $nsisOut = Invoke-CapturedNative { & $NsisPath "/V2" "/INPUTCHARSET" "UTF8" "/DSELF_CONTAINED" "/DAPP_VERSION_FULL=$installerVersion" "/DAPP_VERSION=$ReleaseVersion" "V-Notch-Setup.nsi" }
} else {
    $nsisOut = Invoke-CapturedNative { & $NsisPath "/V2" "/INPUTCHARSET" "UTF8" "/DAPP_VERSION_FULL=$installerVersion" "/DAPP_VERSION=$ReleaseVersion" "V-Notch-Setup.nsi" }
}
if ($LASTEXITCODE -ne 0) {
    foreach ($l in @($nsisOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) {
        Write-TreeItem $l.Trim() -State "Error"
    }
    Write-TreeItem "NSIS build failed!" -State "Error"
    Write-Host "  $gBotLeft$lineRule`n" -ForegroundColor DarkCyan
    exit 1
}
Write-TreeItem "Built installers\V-Notch-Setup.exe." -State "Success"

# Step 4: Signing & Checksums
Write-TreeSpacer
Write-TreeStep "4/4" "Signatures & SHA-256 Checksums"

if ($CertificateThumbprint) {
    & (Join-Path $scriptDir 'Sign-ReleaseBinary.ps1') -Path 'installers\V-Notch-Setup.exe' -CertificateThumbprint $CertificateThumbprint
    Write-TreeItem "Installer Authenticode signature applied (thumbprint)." -State "Success"
} elseif ($CertificatePath) {
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) { Write-TreeItem "signtool.exe not found; cannot sign installer." -State "Error"; exit 1 }
    & $signtool.Source sign /fd SHA256 /f $CertificatePath /p $CertificatePassword /tr "http://timestamp.digicert.com" /td SHA256 "installers\V-Notch-Setup.exe"
    if ($LASTEXITCODE -ne 0) { Write-TreeItem "Authenticode signing failed!" -State "Error"; exit 1 }
    Write-TreeItem "Installer Authenticode signature applied." -State "Success"
} else {
    Write-TreeItem "No Authenticode certificate (optional)." -State "Info"
}

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
    Write-TreeItem "Failed to calculate SHA-256 due to file lock!" -State "Error"
    exit 1
}

Set-Content -Path "installers\V-Notch-Setup.exe.sha256" -Value "$checksum  V-Notch-Setup.exe"
$exeShaName = if ($SelfContained) { "V-Notch-SelfContained.exe.sha256" } else { "V-Notch.exe.sha256" }
Set-Content -Path "installers\$exeShaName" -Value "$appExeHash  V-Notch.exe"
Write-TreeItem "SHA-256: $checksum" -State "Success"

$privateKeyCandidates = @(
    $env:VNOTCH_UPDATE_SIGNING_KEY_PEM_PATH,
    (Join-Path $repoRoot "..\VNotchReleaseKeys\update-2026-09-private.pem")
)
$foundKey = $privateKeyCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($foundKey -or $env:VNOTCH_UPDATE_SIGNING_KEY_PEM -or $env:VNOTCH_UPDATE_SIGNING_PRIVATE_KEY_PEM) {
    $signScript = Join-Path $scriptDir "Sign-UpdateManifest.ps1"
    $pwshCmd = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    $keyArg = if ($foundKey) { "-PrivateKeyPath '$foundKey'" } else { "" }
    try {
        if ($PSVersionTable.PSVersion.Major -ge 7) {
            $signParams = @{
                InstallerPath = "installers\V-Notch-Setup.exe"
                Version = $projectVersion
            }
            if ($foundKey) { $signParams["PrivateKeyPath"] = $foundKey }
            & $signScript @signParams | Out-Null
        } elseif ($pwshCmd) {
            & $pwshCmd.Source -NoProfile -ExecutionPolicy Bypass -Command "& '$signScript' -InstallerPath 'installers\V-Notch-Setup.exe' -Version '$projectVersion' $keyArg" | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Sign-UpdateManifest.ps1 failed with exit code $LASTEXITCODE."
            }
        } else {
            throw "PowerShell 7 (pwsh) is required to sign the ECDSA update manifest when VNOTCH_UPDATE_SIGNING_PRIVATE_KEY_PEM is set."
        }
        Write-TreeItem "Signed ECDSA update manifest." -State "Success"
    } catch {
        Write-TreeItem "Could not sign update manifest: $_" -State "Error"
        throw
    }
}

if ($env:GITHUB_ACTIONS -ne 'true') {
    foreach ($tempDir in @($publishDir, 'Uninstall\bin', 'Uninstall\obj')) {
        $fullTemp = [IO.Path]::GetFullPath((Join-Path $repoRoot $tempDir))
        if ($fullTemp.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $fullTemp)) {
            Remove-Item -LiteralPath $fullTemp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Write-TreeItem "Cleaned intermediate '$publishDir' & Uninstall build directories." -State "Success"
}

$totalSw.Stop()
Write-TreeSpacer
Write-Host "  $gBotLeft$lineRule" -ForegroundColor DarkCyan
Write-Host "     $gCheck BUILD COMPLETE " -ForegroundColor Green -NoNewline
Write-Host ("v$exeVersion -> installers\V-Notch-Setup.exe ({0:N1}s)" -f $totalSw.Elapsed.TotalSeconds) -ForegroundColor Gray
Write-Host ""

} finally {
    Pop-Location
}
