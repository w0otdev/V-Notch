#Requires -Version 7.0
[CmdletBinding()]
param([string]$AssetsDirectory)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
# Immutable source of the public 1.9.3 client, rather than today's updater.
$legacyCommit = 'daddcb4e711adb71c215ba7b13f77b511c6a7804'
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
        [IO.File]::WriteAllText((Join-Path $temporary "$name.cs"), (Read-Legacy "Services/$name.cs"))
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
    <AssemblyVersion>1.9.3.0</AssemblyVersion>
  </PropertyGroup>
  <ItemGroup><EmbeddedResource Include="update-public-key.pem" LogicalName="VNotch.UpdatePublicKey.pem" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $temporary 'Compatibility.csproj')
    [xml]$project = Get-Content -LiteralPath (Join-Path $repository 'V-Notch.csproj')
    $version = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    $arguments = @('run', '--project', (Join-Path $temporary 'Compatibility.csproj'), '-c', 'Release', '--', $version)
    if ($AssetsDirectory) { $arguments += (Resolve-Path -LiteralPath $AssetsDirectory).Path }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'The 1.9.3 update compatibility check failed.' }
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    if ([IO.Path]::GetFileName($resolved) -notlike 'vnotch-updater-193-*' -or
        [IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetTempPath().TrimEnd('\', '/')) {
        throw 'Unsafe compatibility-test cleanup path.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
