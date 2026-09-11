using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ShaderCompiler;

public static class Program
{
    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern int D3DCompile(
        byte[] srcData,
        IntPtr srcDataLen,
        string srcName,
        IntPtr defines,
        IntPtr include,
        string entryPoint,
        string target,
        uint flags1,
        uint flags2,
        out IntPtr code,
        out IntPtr errorMsgs);

    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] IntPtr GetBufferSize();
    }

    public static int Main(string[] args)
    {
        var positionalArgs = Array.FindAll(args, a => !a.StartsWith('-'));
        string hlslPath = positionalArgs.Length > 0 ? positionalArgs[0] : "Shaders/LiquidGlassRefraction.hlsl";
        string psPath = positionalArgs.Length > 1 ? positionalArgs[1] : "Shaders/LiquidGlassRefraction.ps";

        if (!File.Exists(hlslPath))
        {
            // If executed from Tools/ShaderCompiler, check repo root
            string candidate = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", hlslPath);
            if (File.Exists(candidate))
            {
                hlslPath = Path.GetFullPath(candidate);
                psPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", psPath));
            }
        }

        if (!File.Exists(hlslPath))
        {
            Console.Error.WriteLine($"[ShaderCompiler] Source HLSL file not found: {hlslPath}");
            return 1;
        }

        byte[] src = File.ReadAllBytes(hlslPath);
        int hr = D3DCompile(src, (IntPtr)src.Length, Path.GetFileName(hlslPath), IntPtr.Zero, IntPtr.Zero, "main", "ps_3_0", 0, 0, out IntPtr codePtr, out IntPtr errPtr);
        if (hr != 0)
        {
            if (errPtr != IntPtr.Zero)
            {
                var blob = (ID3DBlob)Marshal.GetObjectForIUnknown(errPtr);
                byte[] err = new byte[blob.GetBufferSize().ToInt32()];
                Marshal.Copy(blob.GetBufferPointer(), err, 0, err.Length);
                Console.Error.WriteLine(Encoding.ASCII.GetString(err));
            }
            return hr;
        }

        var codeBlob = (ID3DBlob)Marshal.GetObjectForIUnknown(codePtr);
        byte[] bytes = new byte[codeBlob.GetBufferSize().ToInt32()];
        Marshal.Copy(codeBlob.GetBufferPointer(), bytes, 0, bytes.Length);

        string? outDir = Path.GetDirectoryName(psPath);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        File.WriteAllBytes(psPath, bytes);
        Console.WriteLine($"[ShaderCompiler] Successfully compiled '{hlslPath}' -> '{psPath}' ({bytes.Length} bytes)");
        return 0;
    }
}
