param(
    [Parameter(Mandatory=$true)][string]$ModelDirectory,
    [string]$RuntimeDll = (Join-Path $PSScriptRoot '../../Native/out/translation/VNotch.Translation.dll'),
    [int]$Threads = 4,
    [switch]$Facade,
    [switch]$AllLanguages,
    [switch]$DefinitionsOnly,
    [switch]$CheckIdle,
    [switch]$Fast
)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
public static class LiteRuntimeProbe {
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32", CharSet=CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet=CharSet.Unicode)]
    delegate int Open(string root, int length, int threads, out long handle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate byte Cancel(IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet=CharSet.Unicode)]
    delegate int Translate(long handle, string text, int length, string target, int targetLength,
        Cancel cancel, IntPtr state, IntPtr output, int capacity, out int written);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Close(long handle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseIdle(long handle, long timeoutMs, out byte released);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetNatural(long handle, byte enabled);
    static Open open; static Translate translate; static Close close;
    static ReleaseIdle releaseIdle;
    static SetNatural setNatural;
    public static long Handle;
    public static void Load(string dll, bool facade) {
        var module = LoadLibraryEx(dll, IntPtr.Zero, 0x1100);
        if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        string prefix = facade ? "vn_translation_lite_" : "vn_lite_";
        open = (Open)Marshal.GetDelegateForFunctionPointer(GetProcAddress(module, prefix + "open"), typeof(Open));
        translate = (Translate)Marshal.GetDelegateForFunctionPointer(GetProcAddress(module, prefix + "translate"), typeof(Translate));
        close = (Close)Marshal.GetDelegateForFunctionPointer(GetProcAddress(module, prefix + "close"), typeof(Close));
        var idle = GetProcAddress(module, prefix + "release_idle");
        if (!facade && idle != IntPtr.Zero) releaseIdle = (ReleaseIdle)Marshal.GetDelegateForFunctionPointer(idle, typeof(ReleaseIdle));
        var natural = GetProcAddress(module, prefix + "set_natural");
        if (natural != IntPtr.Zero) setNatural = (SetNatural)Marshal.GetDelegateForFunctionPointer(natural, typeof(SetNatural));
    }
    public static int Start(string path, int threads) { return open(path, path.Length, threads, out Handle); }
    public static void Natural(bool enabled) {
        if (setNatural == null || setNatural(Handle, (byte)(enabled ? 1 : 0)) != 0) throw new Exception("Natural mode configuration failed");
    }
    public static string Run(string text, string target, int cancelAfter, int capacity, int expected) {
        int calls = 0;
        Cancel cancel = state => (byte)(cancelAfter >= 0 && ++calls >= cancelAfter ? 1 : 0);
        IntPtr output = Marshal.AllocHGlobal(Math.Max(1, capacity) * 2);
        try {
            int written;
            int rc = translate(Handle, text, text.Length, target, target.Length, cancel, IntPtr.Zero, output, capacity, out written);
            if (rc != expected) throw new Exception("Expected " + expected + ", received " + rc + " for " + target);
            return rc == 0 ? Marshal.PtrToStringUni(output, written) : "";
        } finally { Marshal.FreeHGlobal(output); GC.KeepAlive(cancel); }
    }
    public static void Stop() { if (close(Handle) != 0) throw new Exception("Close failed"); }
    public static bool Trim(long timeoutMs) {
        if (releaseIdle == null) throw new Exception("Runtime idle API missing");
        byte released;
        int rc = releaseIdle(Handle, timeoutMs, out released);
        if (rc != 0) throw new Exception("Idle release failed: " + rc);
        return released != 0;
    }
}
'@
if ($DefinitionsOnly) { return }
$ModelDirectory = (Resolve-Path -LiteralPath $ModelDirectory).Path
[LiteRuntimeProbe]::Load((Resolve-Path -LiteralPath $RuntimeDll).Path, $Facade.IsPresent)
$clock = [Diagnostics.Stopwatch]::StartNew()
$status = [LiteRuntimeProbe]::Start($ModelDirectory, $Threads)
if ($status -ne 0) { throw "Model load failed: $status" }
if ($Fast) { [LiteRuntimeProbe]::Natural($false) }
Write-Output ('Cold load + warmup: {0:N0} ms' -f $clock.Elapsed.TotalMilliseconds)
try {
    $cases = @(
        @('en-vi', 'Where is the nearest train station?', 'vi'),
        @('vi-ja', 'Tôi muốn đặt một phòng cho hai người.', 'ja'),
        @('ja-fr', '明日の会議は何時に始まりますか？', 'fr'),
        @('zh-de', '请告诉我如何到达机场。', 'de'),
        @('ar-en', 'أين يمكنني شراء تذكرة القطار؟', 'en'),
        @('fr-es', 'Le restaurant est ouvert tous les jours.', 'es'),
        @('en-tl', 'Thank you for your help.', 'tl'),
        @('en-ns', 'Thank you for your help.', 'ns'),
        @('en-ast', 'Good morning. How are you?', 'ast')
    )
    foreach ($case in $cases) {
        $clock.Restart()
        $translated = [LiteRuntimeProbe]::Run($case[1], $case[2], -1, 65536, 0)
        if ([string]::IsNullOrWhiteSpace($translated)) { throw "Empty translation: $($case[0])" }
        Write-Output ('{0}: {1:N0} ms | {2}' -f $case[0],$clock.Elapsed.TotalMilliseconds,$translated)
    }
    [void][LiteRuntimeProbe]::Run('Hello world.', 'xx', -1, 65536, -21)
    [void][LiteRuntimeProbe]::Run('Hello world.', 'fr', 1, 65536, -20)
    [void][LiteRuntimeProbe]::Run(('This is a longer sentence to cancel during decoding. ' * 10), 'fr', 6, 65536, -20)
    $clock.Restart()
    [void][LiteRuntimeProbe]::Run('Please check the train schedule and the hotel address before leaving.', 'fr', 8, 65536, -20)
    Write-Output ('Cancellation return: {0:N0} ms' -f $clock.Elapsed.TotalMilliseconds)
    [void][LiteRuntimeProbe]::Run('Hello world.', 'fr', -1, 1, -2)
    [void][LiteRuntimeProbe]::Run(('x' * 16385), 'fr', -1, 65536, -24)
    $lines = [LiteRuntimeProbe]::Run("  Good morning.`r`n`r`nThank you.`t", 'fr', -1, 65536, 0)
    if (!$lines.StartsWith('  ') -or !$lines.EndsWith("`t") -or !$lines.Contains("`r`n`r`n")) { throw 'Line formatting lost' }
    # Cross more than one inference batch with distinct indentation and repeated content.
    $sentences = @('Open the document.', 'Save your changes.', 'Please check the train schedule.',
        'Where is the nearest bus station?', 'Thank you for your help.', 'Close the window.',
        'The restaurant is open every day.', 'We will discuss the results tomorrow.', 'Open the document.')
    $indented = for ($i=0; $i -lt $sentences.Count; $i++) { (' ' * ($i+1)) + $sentences[$i] + "`t" }
    $batchLines = [LiteRuntimeProbe]::Run(($indented -join "`r`n"), 'vi', -1, 65536, 0).Split("`r`n", [StringSplitOptions]::None)
    if ($batchLines.Count -ne $sentences.Count) { throw 'Batch changed line count' }
    for ($i=0; $i -lt $batchLines.Count; $i++) {
        if (!$batchLines[$i].StartsWith(' ' * ($i+1)) -or $batchLines[$i].StartsWith(' ' * ($i+2)) -or
            !$batchLines[$i].EndsWith("`t") -or [string]::IsNullOrWhiteSpace($batchLines[$i])) { throw "Batch line formatting lost: $i" }
    }
    if ($AllLanguages) {
        $vocabulary = Get-Content -LiteralPath (Join-Path $ModelDirectory 'shared_vocabulary.json') -Raw | ConvertFrom-Json
        $languages = @($vocabulary | Where-Object { $_ -match '^__[a-z]{2,3}__$' } | ForEach-Object { $_.Trim('_') })
        foreach ($language in $languages) {
            $translated = [LiteRuntimeProbe]::Run('Thank you for your help.', $language, -1, 65536, 0)
            if ([string]::IsNullOrWhiteSpace($translated)) { throw "Empty translation: $language" }
        }
        Write-Output ("Language smoke checks passed: " + $languages.Count + ' (nonempty output; not a quality benchmark)')
    }
    Write-Output ('Cancellation, bounds and line formatting passed. Peak working set: {0:N0} MiB' -f ([Diagnostics.Process]::GetCurrentProcess().PeakWorkingSet64 / 1MB))
    if ($CheckIdle) {
        if ($Facade) { throw 'CheckIdle exercises the runtime millisecond API; omit Facade.' }
        if ([LiteRuntimeProbe]::Trim(0) -or [LiteRuntimeProbe]::Trim(60000)) { throw 'Active model released too early' }
        Start-Sleep -Milliseconds 20
        if (![LiteRuntimeProbe]::Trim(1)) { throw 'Expired idle model was not released' }
        [void][LiteRuntimeProbe]::Run('Hello', 'fr', -1, 65536, -1)
        if ([LiteRuntimeProbe]::Start($ModelDirectory, $Threads) -ne 0) { throw 'Reload after idle failed' }
        [void][LiteRuntimeProbe]::Run('Hello', 'fr', -1, 65536, 0)
        Write-Output 'Idle retention, release and reload passed.'
    }
} finally { [LiteRuntimeProbe]::Stop() }
