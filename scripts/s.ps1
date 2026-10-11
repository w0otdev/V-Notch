$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path $repoRoot "Tools\ShaderCompiler\ShaderCompiler.csproj"
$sourcePath = Join-Path $repoRoot "Shaders\LiquidGlassRefraction.hlsl"
$outputPath = Join-Path $repoRoot "Shaders\LiquidGlassRefraction.ps"

dotnet run --project $projectPath -- $sourcePath $outputPath
exit $LASTEXITCODE
