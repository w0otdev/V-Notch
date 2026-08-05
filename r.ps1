param(
    # Use -Clean to force a full rebuild (e.g. when BAML/animations are suspected stale).
    # Defaults to a fast incremental build.
    [switch]$Clean
)

# Set execution policy for the current process scope to Bypass to prevent script execution block
Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force

$ErrorActionPreference = "Continue"

# --- Configuration ---------------------------------------------------------
$processName = "V-Notch"
$objPath     = "obj\Debug\net8.0-windows10.0.19041.0\win-x64"
$binPath     = "bin\Debug\net8.0-windows10.0.19041.0\win-x64"
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
    taskkill /F /IM "$processName.exe" /T 2>$null | Out-Null

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

$buildArgs = @("build", "V-Notch.csproj", "-nologo")
$restoreReady = Test-Path "obj\project.assets.json"
if (-not $Clean -and $restoreReady) {
    $buildArgs += "--no-restore"
}

dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[!] Build failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

# --- 4. Launch --------------------------------------------------------------
Write-Host ">>> Launching V-Notch (press Ctrl+C to stop)..." -ForegroundColor Green
try {
    if (Test-Path $exePath) {
        Write-Host ">>> Launching: $exePath" -ForegroundColor Gray
        $proc = Start-Process -FilePath $exePath -PassThru
        $proc | Wait-Process
    } else {
        Write-Host ">>> Exe not found, running via dotnet run --no-build..." -ForegroundColor Yellow
        dotnet run --no-build
    }
}
finally {
    Write-Host "`n>>> Cleaning up task..." -ForegroundColor Yellow
    taskkill /F /IM "$processName.exe" /T 2>$null | Out-Null
    Stop-Process -Name $processName -Force -ErrorAction SilentlyContinue
}
