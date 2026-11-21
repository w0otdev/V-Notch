# Build V-Notch Installer
# This script builds the Release version and creates the NSIS installer
#
# By default it produces a framework-dependent build (needs .NET 10 Desktop Runtime).
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
    [string]$TestedNativeArtifactDirectory = '',
    # Optional locally. The GitHub release workflow requires a pinned signer.
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

# Use the pinned publisher for a plain ./b. An explicit PFX selects its own signer.
if (-not $PSBoundParameters.ContainsKey('CertificateThumbprint') -and -not $CertificatePath) {
    $CertificateThumbprint = $env:VNOTCH_CODE_SIGNING_THUMBPRINT
}
if ($CertificateThumbprint) {
    $CertificateThumbprint = $CertificateThumbprint -replace '\s', ''
    if ($CertificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$') {
        throw 'The release certificate thumbprint must contain 40 hexadecimal characters.'
    }
    if (-not (Test-Path -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint")) {
        throw 'The configured release certificate was not found in the current user Personal store (Cert:\CurrentUser\My).'
    }
}
if ($RequireAuthenticode -and -not $CertificateThumbprint) {
    throw 'A trusted certificate thumbprint is required for a public release.'
}

# Capture the conditional output as an array: a single string splats into
# individual characters when passed to a native command such as dotnet.
$restoreArguments = @(if ($LockedRestore) { '-p:RestoreLockedMode=true' })
$nativeArguments = @()
if ($TestedNativeArtifactDirectory) {
    & (Join-Path $repoRoot 'Tools/Assert-NativeArtifact.ps1') -ArtifactDirectory $TestedNativeArtifactDirectory
    $nativeBinary = [IO.Path]::GetFullPath((Join-Path $TestedNativeArtifactDirectory 'VNotch.Native.dll'))
    $nativeArguments = @('-p:SkipNativeCompile=true', "-p:VNotchNativeArtifactPath=$nativeBinary")
    # The tested artifact contains the main DLL. Stage the pinned Lite runtime separately.
    & (Join-Path $scriptDir 'Build-TranslationRuntime.ps1') -Root $repoRoot
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

$modeText  = if ($SelfContained) { "Self-Contained (win-x64)" } else { "Framework-Dependent (.NET 10)" }
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
    # Self-contained: bundles the .NET runtime, runs without installing .NET 10.
    $pubOut = Invoke-CapturedNative { & dotnet publish .\V-Notch.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=none -p:DebugSymbols=false -nologo -v:q -o $publishDir @restoreArguments @versionArguments @nativeArguments }
} else {
    # Framework-dependent single file - requires .NET 10 runtime.
    $pubOut = Invoke-CapturedNative { & dotnet publish .\V-Notch.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=none -p:DebugSymbols=false -nologo -v:q -o $publishDir @restoreArguments @versionArguments @nativeArguments }
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
if ($TestedNativeArtifactDirectory) {
    & (Join-Path $repoRoot 'Tools/Assert-NativeArtifact.ps1') -ArtifactDirectory $TestedNativeArtifactDirectory -BinaryPath "$publishDir\VNotch.Native.dll"
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
    if (-not (Test-Path -LiteralPath $NsisPath)) {
        $nsisCache = Join-Path $repoRoot '.cache/nsis'
        $NsisPath = & (Join-Path $scriptDir 'Initialize-Nsis.ps1') -CacheDirectory $nsisCache -DestinationDirectory (Join-Path $nsisCache 'portable')
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

$nsisArguments = @('/V2', '/INPUTCHARSET', 'UTF8', "/DAPP_VERSION_FULL=$installerVersion", "/DAPP_VERSION=$ReleaseVersion")
if ($SelfContained) { $nsisArguments += '/DSELF_CONTAINED' }

# The BCJ2 filter and solid LZMA2 compress the native code with a 16 MiB
# dictionary. The console extraction module also works on offline Windows 10.
$programFilesRoot = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$sevenZipDir = Join-Path $programFilesRoot '7-Zip'
$sevenZip = Join-Path $sevenZipDir '7z.exe'
$extractor = Join-Path $sevenZipDir '7zCon.sfx'
$license = Join-Path $sevenZipDir 'License.txt'
$packedPayload = $null
try {
    if ((Test-Path -LiteralPath $sevenZip) -and (Test-Path -LiteralPath $extractor) -and (Test-Path -LiteralPath $license)) {
        $packedPayload = Join-Path ([IO.Path]::GetTempPath()) ("vnotch-payload-{0}.exe" -f [guid]::NewGuid())
        Copy-Item -LiteralPath $license -Destination (Join-Path $publishDir 'licenses/7-Zip.txt')
        Push-Location $publishDir
        try {
            & $sevenZip a -t7z "-sfx$extractor" -mx=9 -mf=BCJ2 -m0=LZMA2:d=16m -ms=on -mmt=2 -bso0 $packedPayload '.\*'
            if ($LASTEXITCODE -ne 0) { throw '7-Zip payload compression failed.' }
        } finally {
            Pop-Location
        }
        if ($CertificateThumbprint) {
            & (Join-Path $scriptDir 'Sign-ReleaseBinary.ps1') -Path $packedPayload -CertificateThumbprint $CertificateThumbprint
        } elseif ($CertificatePath) {
            & $signtool.Source sign /fd SHA256 /f $CertificatePath /p $CertificatePassword /tr "http://timestamp.digicert.com" /td SHA256 $packedPayload
            if ($LASTEXITCODE -ne 0) { throw 'Authenticode signing of the extraction module failed.' }
        }
        $nsisArguments += "/DPACKED_PAYLOAD=$packedPayload"
    } else {
        Write-TreeItem 'Using NSIS compression. Install 7-Zip for a smaller setup executable.'
    }
    # A new installer invalidates the previous update metadata even if signing fails.
    foreach ($sidecar in 'installers\V-Notch-Setup.exe.manifest.sig', 'installers\V-Notch-Setup.exe.manifest.json') {
        if (Test-Path -LiteralPath $sidecar) { Remove-Item -LiteralPath $sidecar }
    }
    $nsisOut = Invoke-CapturedNative { & $NsisPath @nsisArguments 'V-Notch-Setup.nsi' }
    $nsisExitCode = $LASTEXITCODE
} finally {
    if ($packedPayload -and (Test-Path -LiteralPath $packedPayload)) {
        Remove-Item -LiteralPath $packedPayload -Force
    }
}
if ($nsisExitCode -ne 0) {
    foreach ($l in @($nsisOut | ForEach-Object { "$_" } | Where-Object { $_.Trim() })) {
        Write-TreeItem $l.Trim() -State "Error"
    }
    Write-TreeItem "NSIS build failed!" -State "Error"
    Write-Host "  $gBotLeft$lineRule`n" -ForegroundColor DarkCyan
    exit 1
}
$installerMiB = (Get-Item -LiteralPath 'installers\V-Notch-Setup.exe').Length / 1MB
Write-TreeItem ("Built installers\V-Notch-Setup.exe ({0:N2} MiB)." -f $installerMiB) -State "Success"

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
    Write-TreeItem "Built without Authenticode. Windows Smart App Control may block VNotch.Native.dll." -State "Warn"
}
if ($CertificateThumbprint -or $CertificatePath) {
    & (Join-Path $scriptDir 'Assert-InstallerSignatures.ps1') -InstallerPath 'installers\V-Notch-Setup.exe' -CertificateThumbprint $CertificateThumbprint
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
    try {
        $signParams = @{
            InstallerPath = "installers\V-Notch-Setup.exe"
            Version = $ReleaseVersion
            RequireAuthenticode = $RequireAuthenticode
        }
        if ($foundKey) { $signParams["PrivateKeyPath"] = $foundKey }
        & $signScript @signParams | Out-Null
        Write-TreeItem "Signed ECDSA update manifest." -State "Success"
    } catch {
        Write-TreeItem "Could not sign update manifest: $_" -State "Error"
        throw
    }
} else {
    Write-TreeItem "Update manifest skipped (no update-signing key configured)." -State "Info"
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
