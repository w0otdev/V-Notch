[CmdletBinding()]
param([string]$AssetsDirectory, [string[]]$ReleaseVersion, [switch]$Prerelease, [string]$InstalledVersion, [string]$ClientRef)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    $pwsh = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    if (-not $pwsh) {
        throw 'PowerShell 7 (pwsh) is required for Assert-UpdateCompatibility.ps1.'
    }
    $pwshArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if ($AssetsDirectory) { $pwshArgs += '-AssetsDirectory', $AssetsDirectory }
    if ($ReleaseVersion) { $pwshArgs += '-ReleaseVersion'; $pwshArgs += $ReleaseVersion }
    if ($Prerelease) { $pwshArgs += '-Prerelease' }
    if ($InstalledVersion) { $pwshArgs += '-InstalledVersion', $InstalledVersion }
    if ($ClientRef) { $pwshArgs += '-ClientRef', $ClientRef }
    & $pwsh.Source @pwshArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Assert-UpdateCompatibility.ps1 failed with exit code $LASTEXITCODE."
    }
    return
}
$useCurrentUpdater = $Prerelease -or [bool]$InstalledVersion
if ($InstalledVersion -and $InstalledVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw 'Invalid installed release version for the compatibility probe.'
}
$repository = Split-Path $PSScriptRoot -Parent
# Immutable source of the public 1.9.3 client, rather than today's updater.
$legacyCommit = 'daddcb4e711adb71c215ba7b13f77b511c6a7804'
if ($ClientRef) {
    $resolvedClient = & git -C $repository rev-parse --verify "${ClientRef}^{commit}"
    if ($LASTEXITCODE -ne 0) { throw 'Installed updater source ref could not be resolved.' }
    if (-not $InstalledVersion) { throw '-InstalledVersion is required with -ClientRef.' }
    $legacyCommit = $resolvedClient
    $useCurrentUpdater = $false
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('vnotch-updater-193-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
function Read-Legacy([string]$Path) {
    $lines = & git -C $repository show "${legacyCommit}:$Path"
    if ($LASTEXITCODE -ne 0) { throw 'The pinned 1.9.3 commit is required. Checkout with fetch-depth: 0.' }
    return $lines -join "`n"
}
try {
    $legacyPem = Read-Legacy 'Security/update-public-key.pem'
    $currentPem = [IO.File]::ReadAllText((Join-Path $repository 'Security/update-public-key.pem'))
    $oldKey = [Security.Cryptography.ECDsa]::Create()
    $newKey = [Security.Cryptography.ECDsa]::Create()
    try {
        $oldKey.ImportFromPem($legacyPem)
        $newKey.ImportFromPem($currentPem)
        if ($oldKey.ExportSubjectPublicKeyInfoPem() -cne $newKey.ExportSubjectPublicKeyInfoPem()) {
            throw 'Update public key changed: installed 1.9.3 clients cannot verify this release.'
        }
    } finally { $oldKey.Dispose(); $newKey.Dispose() }

    foreach ($name in 'UpdateService', 'IUpdateService', 'SignedUpdateManifest', 'UpdateSecurityPolicy', 'AppIntegrityService') {
        $source = if ($useCurrentUpdater) { [IO.File]::ReadAllText((Join-Path $repository "Services/$name.cs")) } else { Read-Legacy "Services/$name.cs" }
        [IO.File]::WriteAllText((Join-Path $temporary "$name.cs"), $source)
    }
    if ($ClientRef) {
        [IO.File]::WriteAllText((Join-Path $temporary 'AppVersion.cs'), (Read-Legacy 'Services/AppVersion.cs'))
        $null = & git -C $repository cat-file -e "${legacyCommit}:Services/NativeWinHttpHandler.cs" 2>$null
        if ($LASTEXITCODE -eq 0) {
            foreach ($dependency in 'NativeWinHttpHandler', 'Win32Interop', 'Win32Interop.Process', 'WindowAnimationPolicy') {
                [IO.File]::WriteAllText((Join-Path $temporary "$dependency.cs"), (Read-Legacy "Services/$dependency.cs"))
            }
            # Preserve binary bytes and the installed client's native trust policy.
            # Redirect stdin/stderr as well so git.exe never inherits Start-Job's
            # pwsh -ServerMode IPC pipe and deadlocks Wait-Job in CI.
            $start = [Diagnostics.ProcessStartInfo]::new((Get-Command git).Source)
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardInput = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $start.ArgumentList.Add('-C'); $start.ArgumentList.Add($repository)
            $start.ArgumentList.Add('--no-pager')
            $start.ArgumentList.Add('show'); $start.ArgumentList.Add("${legacyCommit}:Native/out/VNotch.Native.dll")
            $process = [Diagnostics.Process]::Start($start)
            $process.StandardInput.Close()
            $file = [IO.File]::Create((Join-Path $temporary 'VNotch.Native.dll'))
            try { $process.StandardOutput.BaseStream.CopyTo($file) } finally { $file.Dispose() }
            $null = $process.StandardError.ReadToEnd()
            $process.WaitForExit()
            if ($process.ExitCode -ne 0) { throw 'The installed client native library is required.' }
            $process.Dispose()
        }
    }
    if ($useCurrentUpdater) {
        Copy-Item -LiteralPath (Join-Path $repository 'Services/AppVersion.cs') -Destination $temporary
        Copy-Item -LiteralPath (Join-Path $repository 'Services/NativeWinHttpHandler.cs') -Destination $temporary
        Copy-Item -LiteralPath (Join-Path $repository 'Services/Win32Interop.cs') -Destination $temporary
        Copy-Item -LiteralPath (Join-Path $repository 'Services/Win32Interop.Process.cs') -Destination $temporary
        Copy-Item -LiteralPath (Join-Path $repository 'Services/WindowAnimationPolicy.cs') -Destination $temporary
        Copy-Item -LiteralPath (Join-Path $repository 'Native/out/VNotch.Native.dll') -Destination $temporary
    }
    [IO.File]::WriteAllText((Join-Path $temporary 'update-public-key.pem'), $legacyPem)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'UpdateCompatibility/Program.cs') -Destination $temporary
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'UpdateCompatibility/TestSupport.cs') -Destination $temporary
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><UseWPF>true</UseWPF>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <AssemblyVersion>1.9.3.0</AssemblyVersion>
    <Version>1.9.3</Version>
  </PropertyGroup>
  <ItemGroup>
    <EmbeddedResource Include="update-public-key.pem" LogicalName="VNotch.UpdatePublicKey.pem" />
    <Content Include="VNotch.Native.dll" Condition="Exists('VNotch.Native.dll')" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $temporary 'Compatibility.csproj')
    if ($InstalledVersion) {
        # Stamp the real assembly instead of injecting a fake CurrentVersion into the updater.
        # A beta's numeric Windows build can exceed the final release's file version.
        $core = [Version](($InstalledVersion -split '[-+]')[0])
        [xml]$probe = Get-Content -LiteralPath (Join-Path $temporary 'Compatibility.csproj')
        $informational = $probe.CreateElement('InformationalVersion')
        $informational.InnerText = $InstalledVersion
        $probe.Project.PropertyGroup.AppendChild($informational) | Out-Null
        $fileVersion = $probe.CreateElement('FileVersion')
        $fileVersion.InnerText = "$($core.Major).$($core.Minor).65534.65534"
        $probe.Project.PropertyGroup.AppendChild($fileVersion) | Out-Null
        $probe.Save((Join-Path $temporary 'Compatibility.csproj'))
    }
    [xml]$project = Get-Content -LiteralPath (Join-Path $repository 'V-Notch.csproj')
    $versions = if ($ReleaseVersion -and $ReleaseVersion.Count -gt 0) {
        @($ReleaseVersion | Where-Object { $_ })
    } else {
        @([string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1))
    }
    $probeProject = Join-Path $temporary 'Compatibility.csproj'
    & dotnet build $probeProject -c Debug -nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'The update compatibility probe build failed.' }
    $probeDll = Join-Path $temporary 'bin/Debug/net8.0-windows10.0.19041.0/Compatibility.dll'
    foreach ($version in $versions) {
        $arguments = @('exec', $probeDll, $version)
        if ($AssetsDirectory) { $arguments += (Resolve-Path -LiteralPath $AssetsDirectory).Path }
        if ($Prerelease) { $arguments += '--prerelease' }
        if ($InstalledVersion) { $arguments += "--installed-version=$InstalledVersion" }
        if ($ClientRef) { $arguments += "--client-ref=$ClientRef" }
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw 'The update compatibility check failed.' }
    }
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    if ([IO.Path]::GetFileName($resolved) -notlike 'vnotch-updater-193-*' -or
        [IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetTempPath().TrimEnd('\', '/')) {
        throw 'Unsafe compatibility-test cleanup path.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
