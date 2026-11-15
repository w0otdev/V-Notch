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
# Compile once during the build. Runtime device creation must never invoke the
# HLSL compiler (in particular, not on WPF's dispatcher). Use the same compiler,
# profiles and strict/O3 flags as the former runtime path, without requiring fxc.
if (-not ('VNotch.Build.GlassShaderCompiler' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace VNotch.Build {
    public static class GlassShaderCompiler {
        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int D3DCompile(byte[] source, UIntPtr size,
            [MarshalAs(UnmanagedType.LPStr)] string name, IntPtr defines, IntPtr include,
            [MarshalAs(UnmanagedType.LPStr)] string entry,
            [MarshalAs(UnmanagedType.LPStr)] string profile, uint flags, uint flags2,
            out IntPtr code, out IntPtr errors);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr GetPointer(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate UIntPtr GetSize(IntPtr self);
        private static byte[] ReadBlob(IntPtr blob) {
            IntPtr table = Marshal.ReadIntPtr(blob);
            var pointer = (GetPointer)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(table, 3 * IntPtr.Size), typeof(GetPointer));
            var size = (GetSize)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(table, 4 * IntPtr.Size), typeof(GetSize));
            var bytes = new byte[checked((int)size(blob).ToUInt64())];
            Marshal.Copy(pointer(blob), bytes, 0, bytes.Length);
            return bytes;
        }
        public static byte[] Compile(string source, string entry, string profile) {
            byte[] input = Encoding.UTF8.GetBytes(source);
            IntPtr code = IntPtr.Zero, errors = IntPtr.Zero;
            try {
                int hr = D3DCompile(input, (UIntPtr)input.Length, "GlassGpu", IntPtr.Zero,
                    IntPtr.Zero, entry, profile, (1u << 15) | (1u << 11), 0, out code, out errors);
                if (hr < 0) throw new InvalidOperationException(entry + ": " +
                    (errors != IntPtr.Zero ? Encoding.UTF8.GetString(ReadBlob(errors)) :
                        "D3DCompile failed: 0x" + hr.ToString("X8")));
                return ReadBlob(code);
            } finally {
                if (code != IntPtr.Zero) Marshal.Release(code);
                if (errors != IntPtr.Zero) Marshal.Release(errors);
            }
        }
    }
}
'@
}
$header = [Text.StringBuilder]::new()
[void]$header.AppendLine('// Generated shader bytecode. Do not edit; see Generate-GlassShaders.ps1.')
[void]$header.AppendLine('#pragma once')
[void]$header.AppendLine('namespace vnotch::graphics::shaders {')
foreach ($entry in @('fullscreenVs', 'cropPs', 'downPs', 'upPs', 'resolvePs', 'copyPs', 'refractPs', 'opaqueWindowPs')) {
    $profile = if ($entry -eq 'fullscreenVs') { 'vs_4_0' } else { 'ps_4_0' }
    $bytes = [VNotch.Build.GlassShaderCompiler]::Compile($shader, $entry, $profile)
    [void]$header.AppendLine("inline constexpr unsigned char $entry[] = {")
    for ($offset = 0; $offset -lt $bytes.Length; $offset += 16) {
        $last = [Math]::Min($offset + 15, $bytes.Length - 1)
        [void]$header.AppendLine('    ' + (($bytes[$offset..$last] | ForEach-Object { '0x{0:X2}' -f $_ }) -join ', ') + ',')
    }
    [void]$header.AppendLine('};')
}
[void]$header.AppendLine('}')
# Failed compilation leaves the previous complete header intact.
$temporary = $Output + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    [IO.File]::WriteAllText($temporary, $header.ToString(), [Text.UTF8Encoding]::new($false))
    if ([IO.File]::Exists($Output)) { [IO.File]::Replace($temporary, $Output, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $Output) }
} finally {
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
