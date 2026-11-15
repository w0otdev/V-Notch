$ErrorActionPreference = 'Stop'
$motionRoot = Split-Path -Parent $PSScriptRoot
$motionOutput = Join-Path $motionRoot 'obj/GlassBackdropMotionProbe'
New-Item -ItemType Directory -Force -Path $motionOutput | Out-Null
$motionVsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$motionVcVars = & $motionVsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find '**/vcvars64.bat' | Select-Object -First 1
if (!$motionVcVars) { throw 'Visual Studio x64 C++ tools are required.' }
$motionSource = Join-Path $motionRoot 'Tools/GlassGpuProbe/BackdropMotionProbe.cpp'
$motionExe = Join-Path $motionOutput 'BackdropMotionProbe.exe'
$motionObj = Join-Path $motionOutput 'BackdropMotionProbe.obj'
$motionBuild = 'call "' + $motionVcVars + '" >nul && cl.exe /nologo /std:c++latest /EHsc /W4 /O2 /MT /I"' + (Join-Path $motionRoot 'Native') + '" /Fo"' + $motionObj + '" /Fe"' + $motionExe + '" "' + $motionSource + '" user32.lib gdi32.lib dwmapi.lib'
& $env:ComSpec /d /c $motionBuild
if ($LASTEXITCODE -ne 0) { throw 'Backdrop motion probe compilation failed.' }
& $motionExe
if ($LASTEXITCODE -ne 0) { throw 'Backdrop motion probe failed.' }
