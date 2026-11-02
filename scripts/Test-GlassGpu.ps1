param([switch]$LiveCapture, [switch]$WindowsGraphicsCapture)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$probeDir = Join-Path $repoRoot 'obj/GlassGpuProbe'
New-Item -ItemType Directory -Force -Path $probeDir | Out-Null
& (Join-Path $PSScriptRoot 'Generate-GlassShaders.ps1') -Root $repoRoot -Output (Join-Path $probeDir 'GlassShaders.g.h')
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vcvars = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find '**/vcvars64.bat' | Select-Object -First 1
if (!$vcvars) { throw 'Visual Studio x64 C++ tools are required.' }
$source = Join-Path $repoRoot 'Tools/GlassGpuProbe/GlassGpuProbe.cpp'
$exe = Join-Path $probeDir 'GlassGpuProbe.exe'
$obj = Join-Path $probeDir 'GlassGpuProbe.obj'
# This generated command only compiles; filesystem operations stay in PowerShell.
$build = 'call "' + $vcvars + '" >nul && cl.exe /nologo /std:c++latest /EHsc /W4 /O2 /MT /I"' + (Join-Path $repoRoot 'Native') + '" /I"' + $probeDir + '" /Fo"' + $obj + '" /Fe"' + $exe + '" "' + $source + '"'
& $env:ComSpec /d /c $build
if ($LASTEXITCODE -ne 0) { throw 'GPU probe compilation failed.' }
if ($WindowsGraphicsCapture) { & $exe '--wgc' }
elseif ($LiveCapture) { & $exe '--live' } else { & $exe }
if ($LASTEXITCODE -ne 0) { throw 'GPU probe failed.' }
