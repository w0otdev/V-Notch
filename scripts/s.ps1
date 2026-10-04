$repoRoot = Split-Path $PSScriptRoot -Parent
dotnet run --project (Join-Path $repoRoot "Tools\ShaderCompiler\ShaderCompiler.csproj")
