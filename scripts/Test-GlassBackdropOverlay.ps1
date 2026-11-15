param([int]$OwnerPid = 0)
$ErrorActionPreference = 'Stop'
$overlayRoot = Split-Path -Parent $PSScriptRoot
$overlayOutput = Join-Path $overlayRoot 'obj/GlassBackdropOverlayProbe'
New-Item -ItemType Directory -Force -Path $overlayOutput | Out-Null
if (!$OwnerPid) {
    $owner = Get-Process -Name 'V-Notch' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($owner) { $OwnerPid = $owner.Id }
}
$overlayVsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$overlayVcVars = & $overlayVsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find '**/vcvars64.bat' | Select-Object -First 1
if (!$overlayVcVars) { throw 'Visual Studio x64 C++ tools are required.' }
$overlaySource = Join-Path $overlayRoot 'Tools/GlassGpuProbe/BackdropCompositeProbe.cpp'
$overlayExe = Join-Path $overlayOutput 'BackdropCompositeProbe.exe'
$overlayObj = Join-Path $overlayOutput 'BackdropCompositeProbe.obj'
$overlayBitmap = Join-Path $overlayOutput 'backdrop.bmp'
$overlayBuild = 'call "' + $overlayVcVars + '" >nul && cl.exe /nologo /std:c++latest /EHsc /W4 /O2 /MT /I"' + (Join-Path $overlayRoot 'Native') + '" /Fo"' + $overlayObj + '" /Fe"' + $overlayExe + '" "' + $overlaySource + '"'
& $env:ComSpec /d /c $overlayBuild
if ($LASTEXITCODE -ne 0) { throw 'Backdrop overlay probe compilation failed.' }
& $overlayExe $OwnerPid $overlayBitmap '--verify-wgc-overlays'
if ($LASTEXITCODE -ne 0) { throw 'Backdrop overlay probe failed.' }
