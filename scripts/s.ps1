$ErrorActionPreference = "Stop"
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
$projectPath = Join-Path $repoRoot "Tools\ShaderCompiler\ShaderCompiler.csproj"
$sourcePath = Join-Path $repoRoot "Shaders\LiquidGlassRefraction.hlsl"
$outputPath = Join-Path $repoRoot "Shaders\LiquidGlassRefraction.ps"

$gTopLeft = [char]0x256D
$gBotLeft = [char]0x2570
$gVert    = [char]0x2502
$gBranch  = [char]0x251C
$gHoriz   = [char]0x2500
$gDiamond = [char]0x25C6
$gCheck   = [char]0x2714
$gCross   = [char]0x2716
$gArrow   = [char]0x25B8
$lineRule = ([string]$gHoriz) * 56

Write-Host ""
Write-Host "  $gTopLeft$lineRule" -ForegroundColor DarkCyan
Write-Host "  $gVert  " -ForegroundColor DarkCyan -NoNewline
Write-Host "$gDiamond V-NOTCH SHADER COMPILER" -ForegroundColor Cyan -NoNewline
Write-Host "  $gVert  " -ForegroundColor DarkGray -NoNewline
Write-Host "HLSL -> PS 3.0" -ForegroundColor Green
Write-Host "  $gVert" -ForegroundColor DarkCyan

Write-Host "  $gBranch$gHoriz " -ForegroundColor DarkCyan -NoNewline
Write-Host "[1/1] " -ForegroundColor Cyan -NoNewline
Write-Host "Compiling LiquidGlassRefraction.hlsl" -ForegroundColor White

Write-Host "  $gVert     $gArrow " -ForegroundColor DarkCyan -NoNewline
Write-Host "Source : Shaders\LiquidGlassRefraction.hlsl" -ForegroundColor Gray
Write-Host "  $gVert     $gArrow " -ForegroundColor DarkCyan -NoNewline
Write-Host "Output : Shaders\LiquidGlassRefraction.ps" -ForegroundColor Gray

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$compileOut = Invoke-CapturedNative { & dotnet run --project $projectPath -- $sourcePath $outputPath }
$exitCode = $LASTEXITCODE
$sw.Stop()

$shaderToolDir = Split-Path -Parent $projectPath
foreach ($sub in @('bin', 'obj')) {
    $subPath = Join-Path $shaderToolDir $sub
    if (Test-Path -LiteralPath $subPath) {
        Remove-Item -LiteralPath $subPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}

foreach ($line in @($compileOut | ForEach-Object { "$_" } | Where-Object { $_.Trim().Length -gt 0 })) {
    Write-Host "  $gVert     " -ForegroundColor DarkCyan -NoNewline
    Write-Host $line.Trim() -ForegroundColor DarkGray
}

if ($exitCode -eq 0) {
    Write-Host "  $gVert     $gCheck " -ForegroundColor Green -NoNewline
    Write-Host ("Shader compiled in {0:N2}s." -f $sw.Elapsed.TotalSeconds) -ForegroundColor Gray
    Write-Host "  $gVert" -ForegroundColor DarkCyan
    Write-Host "  $gBotLeft$lineRule" -ForegroundColor DarkCyan
    Write-Host "     $gCheck DONE " -ForegroundColor Green -NoNewline
    Write-Host "Shaders\LiquidGlassRefraction.ps updated." -ForegroundColor Gray
    Write-Host ""
} else {
    Write-Host "  $gVert     $gCross " -ForegroundColor Red -NoNewline
    Write-Host ("Shader compilation failed (exit code $exitCode).") -ForegroundColor Red
    Write-Host "  $gVert" -ForegroundColor DarkCyan
    Write-Host "  $gBotLeft$lineRule`n" -ForegroundColor DarkCyan
}

exit $exitCode
