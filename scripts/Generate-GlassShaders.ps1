param([Parameter(Mandatory=$true)][string]$Root, [Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference = 'Stop'
$core = [IO.File]::ReadAllText((Join-Path $Root 'Shaders/LiquidGlassRefraction.hlsl'))
# Keep one optical implementation for the WPF fallback and the D3D11 renderer.
$core = $core -replace 'sampler2D input : register\(s0\);', ''
$count = 0
$core = [regex]::Replace($core, 'float\s+(\w+)\s*:\s*register\(c(\d+)\);', {
    param($m)
    $script:count++
    '#define ' + $m.Groups[1].Value + ' lens[' + $m.Groups[2].Value + '].x'
})
if ($count -ne 32) { throw "Expected 32 glass shader constants; found $count." }
$core = $core -replace 'float4 main\(float2 uv : TEXCOORD\) : COLOR', 'float4 glassRefraction(float2 uv)'
$prefix = [IO.File]::ReadAllText((Join-Path $Root 'Shaders/GlassGpu.hlsl'))
$suffix = @'
float4 refractPs(VertexOutput i) : SV_Target
{
    return glassRefraction(i.position.xy / max(float2(srcW, srcH), 1));
}
'@
$shader = $prefix + "`n#define tex2D(s, uv) sampleSource(uv)`n" + $core + "`n" + $suffix
$outputAbsolute = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputAbsolute)) | Out-Null
$sourcePath = $outputAbsolute + '.' + [Guid]::NewGuid().ToString('N') + '.hlsl'
try {
    [IO.File]::WriteAllText($sourcePath, $shader, [Text.UTF8Encoding]::new($false))
    # Compile once at build time, using the same entry points, profiles and flags
    # as the former runtime compiler. Presenters only create device shader objects.
    & dotnet run --project (Join-Path $Root 'Tools/ShaderCompiler/ShaderCompiler.csproj') --configuration Release -- --glass-header $sourcePath $outputAbsolute
    if ($LASTEXITCODE -ne 0) { throw "Glass shader compilation failed (exit $LASTEXITCODE)." }
}
finally {
    if ([IO.File]::Exists($sourcePath)) { [IO.File]::Delete($sourcePath) }
}
