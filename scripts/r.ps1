param(
    # Use -Clean to force a full rebuild (e.g. when BAML/animations are suspected stale).
    # Defaults to a fast incremental build.
    [switch]$Clean
)

Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# Suppress background console window popups & dotnet startup overhead
$env:GIT_TERMINAL_PROMPT = "0"
$env:GCM_INTERACTIVE = "never"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:Path = ($env:Path -split ";" | Where-Object { $_ -ne "C:\Program Files\Git\mingw64\bin" }) -join ";"

# --- Configuration ---------------------------------------------------------
$repoRoot    = Split-Path $PSScriptRoot -Parent
$processName   = "V-Notch"
$csprojPath    = Join-Path $repoRoot "V-Notch.csproj"
$objPath       = Join-Path $repoRoot "obj\Debug\net8.0-windows10.0.19041.0\win-x64"
$nativeObjPath = Join-Path $repoRoot "obj\native"
$binPath       = Join-Path $repoRoot "bin\Debug\net8.0-windows10.0.19041.0\win-x64"
$exePath       = [IO.Path]::GetFullPath((Join-Path $binPath "V-Notch.exe"))
$nativeDllPath = Join-Path $binPath "VNotch.Native.dll"

$version = "dev"
if (Test-Path $csprojPath) {
    try {
        [xml]$projXml = Get-Content -LiteralPath $csprojPath -Raw
        $parsedVer = $projXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
        if ($parsedVer) { $version = "v$parsedVer" }
    } catch { }
}

# --- Runtime Unicode Glyphs (7-bit ASCII source safe for PS 5.1 & PS 7) ----
$gTopLeft     = [char]0x256D # ╭
$gBotLeft     = [char]0x2570 # ╰
$gVert        = [char]0x2502 # │
$gBranch      = [char]0x251C # ├
$gHoriz       = [char]0x2500 # ─
$gDiamond     = [char]0x25C6 # ◆
$gDot         = [char]0x25CF # ●
$gCheck       = [char]0x2714 # ✔
$gCross       = [char]0x2716 # ✖
$gWarn        = [char]0x25B2 # ▲
$gArrow       = [char]0x25B8 # ▸

$lineRule     = ([string]$gHoriz) * 56

function Write-TreeHeader {
    $modeText  = if ($Clean) { "Full Rebuild (-Clean)" } else { "Fast Incremental" }
    $modeColor = if ($Clean) { "Yellow" } else { "Green" }

    Write-Host ""
    Write-Host "  $gTopLeft$lineRule" -ForegroundColor DarkCyan
    Write-Host "  $gVert  " -ForegroundColor DarkCyan -NoNewline
    Write-Host "$gDiamond V-NOTCH DEV RUNNER " -ForegroundColor Cyan -NoNewline
    Write-Host $version -ForegroundColor DarkGray -NoNewline
    Write-Host "  $gVert  " -ForegroundColor DarkGray -NoNewline
    Write-Host $modeText -ForegroundColor $modeColor
    Write-Host "  $gVert" -ForegroundColor DarkCyan
}

function Write-TreeStep {
    param(
        [string]$StepTag,
        [string]$Title
    )
    Write-Host "  $gBranch$gHoriz " -ForegroundColor DarkCyan -NoNewline
    Write-Host "[$StepTag] " -ForegroundColor Cyan -NoNewline
    Write-Host $Title -ForegroundColor White
}

function Write-TreeItem {
    param(
        [string]$Message,
        [ValidateSet("Info", "Success", "Warn", "Error")]
        [string]$State = "Info"
    )
    switch ($State) {
        "Info"    { $sym = $gArrow; $sCol = "DarkCyan"; $tCol = "Gray" }
        "Success" { $sym = $gCheck; $sCol = "Green";    $tCol = "Gray" }
        "Warn"    { $sym = $gWarn;  $sCol = "Yellow";   $tCol = "Yellow" }
        "Error"   { $sym = $gCross; $sCol = "Red";      $tCol = "Red" }
    }
    Write-Host "  $gVert     " -ForegroundColor DarkCyan -NoNewline
    Write-Host "$sym " -ForegroundColor $sCol -NoNewline
    Write-Host $Message -ForegroundColor $tCol
}

function Write-TreeSpacer {
    Write-Host "  $gVert" -ForegroundColor DarkCyan
}

$totalSteps = if ($Clean) { 4 } else { 3 }
$stepIndex  = 0
function Next-StepTag {
    $script:stepIndex++
    return "$($script:stepIndex)/$totalSteps"
}

Write-TreeHeader

# --- 1. Stop old instances and WAIT for them to actually exit ---------------
function Wait-VNotchExit {
    param([int]$TimeoutMs = 7000)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $alive = Get-Process -Name $processName -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exePath }
        if (-not $alive) { return $true }
        Start-Sleep -Milliseconds 150
    }
    return $false
}

Write-TreeStep (Next-StepTag) "Process Check"
$running = @(Get-Process -Name $processName -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exePath })
if ($running.Count -gt 0) {
    $pids = ($running | Select-Object -ExpandProperty Id) -join ", "
    Write-TreeItem "Stopping $($running.Count) running instance(s) (PID: $pids)..." -State Info
    $running | Stop-Process -Force -ErrorAction SilentlyContinue

    if (-not (Wait-VNotchExit -TimeoutMs 7000)) {
        Write-TreeItem "Process still alive after 7s - aborting." -State Error
        Write-Host "  $gBotLeft$lineRule`n" -ForegroundColor DarkCyan
        exit 1
    }
    Write-TreeItem "Terminated cleanly." -State Success
} else {
    Write-TreeItem "No active V-Notch process." -State Success
}

# --- 2. (Only with -Clean) Hard-delete obj/bin to force a full rebuild -------
function Remove-Folder-Hard {
    param([string]$Path, [int]$Retries = 4)
    if (-not (Test-Path $Path)) { return $true }
    for ($i = 0; $i -lt $Retries; $i++) {
        try {
            Remove-Item $Path -Recurse -Force -ErrorAction Stop
            return $true
        } catch {
            if ($i -eq ($Retries - 1)) {
                Write-TreeItem "Could not delete $Path : $($_.Exception.Message)" -State Warn
                return $false
            }
            Start-Sleep -Milliseconds 400
        }
    }
    return $false
}

if ($Clean) {
    Write-TreeSpacer
    Write-TreeStep (Next-StepTag) "Clean Cache"
    $objClean       = Remove-Folder-Hard $objPath
    $nativeObjClean = Remove-Folder-Hard $nativeObjPath
    $binClean       = Remove-Folder-Hard $binPath

    if ($objClean -and $nativeObjClean -and $binClean) {
        Write-TreeItem "Cleared obj, native obj & bin directories (fresh BAML & C++20)." -State Success
    } else {
        Write-TreeItem "Partial cleanup - some files were locked." -State Warn
    }
}

# --- 3. Build ---------------------------------------------------------------
Write-TreeSpacer
$assetsPath   = Join-Path $repoRoot "obj\project.assets.json"
$lockFilePath = Join-Path $repoRoot "packages.lock.json"
$restoreReady = (Test-Path -LiteralPath $assetsPath) -and (-not (Test-Path -LiteralPath $lockFilePath) -or (Get-Item -LiteralPath $assetsPath).LastWriteTimeUtc -ge (Get-Item -LiteralPath $lockFilePath).LastWriteTimeUtc)
$useNoRestore = (-not $Clean -and $restoreReady)
$buildModeTag = if ($Clean) { "Full Rebuild" } elseif ($useNoRestore) { "Incremental --no-restore" } else { "Incremental + Restore" }

Write-TreeStep (Next-StepTag) "Compile ($buildModeTag)"

# shortcut: skips /GL & /LTCG on dev builds, use scripts/b.ps1 or scripts/c.ps1 for LTCG verification
$buildArgs = @("build", $csprojPath, "-nologo", "-v:q", "-clp:NoSummary", "-m", "-p:NativeFastCompile=true", "-p:RunAnalyzers=false")
if ($useNoRestore) {
    $buildArgs += "--no-restore"
}

$buildSw = [System.Diagnostics.Stopwatch]::StartNew()
$buildOutput = & dotnet @buildArgs 2>&1
$buildExitCode = $LASTEXITCODE
$buildSw.Stop()

$diagnosticLines = @($buildOutput | ForEach-Object { "$_" } | Where-Object { $_.Trim().Length -gt 0 })

if ($buildExitCode -ne 0) {
    Write-TreeItem ("Build failed in {0:N2}s (exit code {1}):" -f $buildSw.Elapsed.TotalSeconds, $buildExitCode) -State Error
    foreach ($line in $diagnosticLines) {
        $lineColor = if ($line -match "error\s+[A-Z]+\d+") { "Red" } elseif ($line -match "warning\s+[A-Z]+\d+") { "Yellow" } else { "Gray" }
        Write-Host "  $gVert       $line" -ForegroundColor $lineColor
    }
    Write-Host "  $gBotLeft$lineRule`n" -ForegroundColor DarkCyan
    exit $buildExitCode
}

foreach ($line in $diagnosticLines) {
    if ($line -match "warning\s+[A-Z]+\d+") {
        Write-TreeItem $line.Trim() -State Warn
    }
}
if (-not (Test-Path $nativeDllPath)) {
    Write-TreeItem "VNotch.Native.dll missing from build output directory: $nativeDllPath" -State Error
    Write-Host "  $gBotLeft$lineRule`n" -ForegroundColor DarkCyan
    exit 1
}
Write-TreeItem ("Built V-Notch.dll & VNotch.Native.dll in {0:N2}s." -f $buildSw.Elapsed.TotalSeconds) -State Success

# --- 4. Launch --------------------------------------------------------------
if (-not ("VNotchDesktopLauncher" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class VNotchDesktopLauncher
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static int StartOnDefaultDesktop(string exePath, string workingDir)
    {
        STARTUPINFO si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
        si.lpDesktop = @"WinSta0\Default";
        si.dwFlags = 0x00000001; // STARTF_USESHOWWINDOW
        si.wShowWindow = 1;     // SW_SHOWNORMAL

        PROCESS_INFORMATION pi;
        string cmdLine = "\"" + exePath + "\"";
        if (!CreateProcessW(exePath, cmdLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, workingDir, ref si, out pi))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return (int)pi.dwProcessId;
    }
}
'@ -ErrorAction SilentlyContinue
}

Write-TreeSpacer
Write-TreeStep (Next-StepTag) "Launch Application"
$sessionSw = [System.Diagnostics.Stopwatch]::StartNew()
$procId = $null
try {
    if (Test-Path $exePath) {
        $relExe = $exePath.Substring($repoRoot.Length + 1)
        $workDir = Split-Path $exePath -Parent
        try {
            $procId = [VNotchDesktopLauncher]::StartOnDefaultDesktop($exePath, $workDir)
        } catch {
            $psi = [System.Diagnostics.ProcessStartInfo]::new($exePath)
            $psi.UseShellExecute = $false
            $psi.WorkingDirectory = $workDir
            $procId = ([System.Diagnostics.Process]::Start($psi)).Id
        }
        Write-TreeItem "Binary  : $relExe" -State Info
        Write-TreeItem "Process : PID $procId (WinSta0\Default)" -State Success
        Write-TreeSpacer
        Write-Host "  $gBotLeft$lineRule" -ForegroundColor DarkCyan
        Write-Host "     " -NoNewline
        Write-Host "$gDot LIVE " -ForegroundColor Green -NoNewline
        Write-Host "V-Notch is running. Press " -ForegroundColor Gray -NoNewline
        Write-Host "Ctrl+C" -ForegroundColor Yellow -NoNewline
        Write-Host " to stop." -ForegroundColor Gray
        Write-Host ""
        Wait-Process -Id $procId
    } else {
        Write-TreeItem "Executable missing, running via 'dotnet run --no-build'..." -State Warn
        Write-Host "  $gBotLeft$lineRule`n" -ForegroundColor DarkCyan
        dotnet run --project $csprojPath --no-build
    }
}
finally {
    $sessionSw.Stop()
    if ($procId) {
        Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
    }
    Write-Host "     " -NoNewline
    Write-Host "$gCheck STOPPED " -ForegroundColor DarkGray -NoNewline
    Write-Host ("Session ended cleanly ({0:mm\:ss})." -f $sessionSw.Elapsed) -ForegroundColor DarkGray
    Write-Host ""
}
