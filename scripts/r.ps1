param(
    # Use -Clean to force a full rebuild (e.g. when BAML/animations are suspected stale).
    # Defaults to a fast incremental build.
    [switch]$Clean
)

# Set execution policy for the current process scope to Bypass to prevent script execution block
Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force

$ErrorActionPreference = "Continue"

# Suppress background console window popups from Git / tools
$env:GIT_TERMINAL_PROMPT = "0"
$env:GCM_INTERACTIVE = "never"
$env:Path = ($env:Path -split ";" | Where-Object { $_ -ne "C:\Program Files\Git\mingw64\bin" }) -join ";"

# --- Configuration ---------------------------------------------------------
$repoRoot    = Split-Path $PSScriptRoot -Parent
$processName = "V-Notch"
$objPath     = Join-Path $repoRoot "obj\Debug\net8.0-windows10.0.19041.0\win-x64"
$binPath     = Join-Path $repoRoot "bin\Debug\net8.0-windows10.0.19041.0\win-x64"
$exePath     = Join-Path $binPath "V-Notch.exe"

# --- 1. Stop old instances and WAIT for them to actually exit ---------------
# taskkill / Stop-Process return as soon as the signal is sent, but the OS needs
# a few hundred ms to release the file handles. If the build runs before the
# handles are released, the BAML/exe can be written in a half state -> animations
# break on app boot.
function Wait-VNotchExit {
    param([int]$TimeoutMs = 7000)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $alive = Get-Process -Name $processName -ErrorAction SilentlyContinue
        if (-not $alive) { return $true }
        Start-Sleep -Milliseconds 150
    }
    return $false
}

Write-Host "`n>>> Stopping old V-Notch instances..." -ForegroundColor Cyan
$running = Get-Process -Name $processName -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force -ErrorAction SilentlyContinue

    if (-not (Wait-VNotchExit -TimeoutMs 7000)) {
        Write-Host "[!] Process still alive after 7s - aborting." -ForegroundColor Red
        exit 1
    }
    # Settling pause - Windows often keeps file handles for ~200ms after the process dies.
    Start-Sleep -Milliseconds 250
}

# --- 2. (Only with -Clean) Hard-delete obj/bin to force a full rebuild -------
# Deleting obj/bin on every build forces dotnet to recompile EVERYTHING + restore,
# which is very slow. dotnet's incremental build handles XAML/BAML changes fine in
# the common case, so we only wipe when the user explicitly asks (-Clean).
function Remove-Folder-Hard {
    param([string]$Path, [int]$Retries = 4)
    if (-not (Test-Path $Path)) { return $true }
    for ($i = 0; $i -lt $Retries; $i++) {
        try {
            Remove-Item $Path -Recurse -Force -ErrorAction Stop
            return $true
        } catch {
            if ($i -eq ($Retries - 1)) {
                Write-Host "[!] Could not delete $Path : $($_.Exception.Message)" -ForegroundColor Yellow
                return $false
            }
            Start-Sleep -Milliseconds 400
        }
    }
    return $false
}

if ($Clean) {
    Write-Host ">>> -Clean: Wiping obj/bin to force a full rebuild (avoid stale BAML)..." -ForegroundColor Cyan
    $objClean = Remove-Folder-Hard $objPath
    $binClean = Remove-Folder-Hard $binPath

    if (-not ($objClean -and $binClean)) {
        Write-Host "[!] Build may not be clean - animations could still break." -ForegroundColor Yellow
    }
}

# --- 3. Build ---------------------------------------------------------------
# Incremental when not -Clean -> much faster since only changed files recompile.
# `--no-restore` is used when obj already exists (restore cache present) to skip the
# slow restore step; if obj is missing (first run / after -Clean) we restore normally.
Write-Host ">>> Building$(if ($Clean) { ' (full rebuild)' } else { ' (incremental)' })..." -ForegroundColor Cyan

$buildArgs = @("build", (Join-Path $repoRoot "V-Notch.csproj"), "-nologo", "-tl")
$restoreReady = Test-Path (Join-Path $repoRoot "obj\project.assets.json")
if (-not $Clean -and $restoreReady) {
    $buildArgs += "--no-restore"
}

dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[!] Build failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

# --- 4. Launch --------------------------------------------------------------
Add-Type -TypeDefinition @"
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
        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf<STARTUPINFO>();
        si.lpDesktop = @"WinSta0\Default";
        si.dwFlags = 0x00000001; // STARTF_USESHOWWINDOW
        si.wShowWindow = 1;     // SW_SHOWNORMAL

        string cmdLine = "\"" + exePath + "\"";
        if (!CreateProcessW(exePath, cmdLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, workingDir, ref si, out var pi))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return (int)pi.dwProcessId;
    }
}
"@ -ErrorAction SilentlyContinue

Write-Host ">>> Launching V-Notch (press Ctrl+C to stop)..." -ForegroundColor Green
try {
    if (Test-Path $exePath) {
        Write-Host ">>> Launching: $exePath" -ForegroundColor Gray
        $workDir = Split-Path $exePath -Parent
        try {
            $procId = [VNotchDesktopLauncher]::StartOnDefaultDesktop($exePath, $workDir)
        } catch {
            $psi = [System.Diagnostics.ProcessStartInfo]::new($exePath)
            $psi.UseShellExecute = $false
            $psi.WorkingDirectory = $workDir
            $procId = ([System.Diagnostics.Process]::Start($psi)).Id
        }
        Wait-Process -Id $procId
    } else {
        Write-Host ">>> Exe not found, running via dotnet run --no-build..." -ForegroundColor Yellow
        dotnet run --project (Join-Path $repoRoot "V-Notch.csproj") --no-build
    }
}
finally {
    Write-Host "`n>>> Cleaning up task..." -ForegroundColor Yellow
    Get-Process -Name $processName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
