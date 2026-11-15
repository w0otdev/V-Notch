$ErrorActionPreference = 'Stop'
$cadenceRoot = Split-Path -Parent $PSScriptRoot
$cadenceOutput = Join-Path $cadenceRoot 'obj/GlassBackdropCadenceProbe'
New-Item -ItemType Directory -Force -Path $cadenceOutput | Out-Null
$cadenceVsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$cadenceVcVars = & $cadenceVsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find '**/vcvars64.bat' | Select-Object -First 1
if (!$cadenceVcVars) { throw 'Visual Studio x64 C++ tools are required.' }
$cadenceSource = Join-Path $cadenceRoot 'Tools/GlassGpuProbe/BackdropCadenceProbe.cpp'
$cadenceExe = Join-Path $cadenceOutput 'BackdropCadenceProbe.exe'
$cadenceObj = Join-Path $cadenceOutput 'BackdropCadenceProbe.obj'
$cadenceBuild = 'call "' + $cadenceVcVars + '" >nul && cl.exe /nologo /std:c++latest /EHsc /W4 /O2 /MT /I"' + (Join-Path $cadenceRoot 'Native') + '" /Fo"' + $cadenceObj + '" /Fe"' + $cadenceExe + '" "' + $cadenceSource + '"'
& $env:ComSpec /d /c $cadenceBuild
if ($LASTEXITCODE -ne 0) { throw 'Backdrop cadence probe compilation failed.' }
& $cadenceExe
if ($LASTEXITCODE -ne 0) { throw 'Backdrop cadence probe failed.' }
