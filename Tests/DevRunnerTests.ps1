$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('vnotch-dev-runner-' + [guid]::NewGuid().ToString('N'))
$fixtures = @()
New-Item -ItemType Directory -Path $temporary | Out-Null

try {
    # Run the production stop phase only, without building or launching V-Notch.
    $runner = Get-Content -LiteralPath (Join-Path $repository 'scripts/r.ps1') -Raw
    $start = $runner.IndexOf('# --- 1. Stop old instances', [StringComparison]::Ordinal)
    $end = $runner.IndexOf('# --- 2.', $start, [StringComparison]::Ordinal)
    if ($start -lt 0 -or $end -le $start) { throw 'Dev runner process-check phase was not found.' }
    $processCheck = [scriptblock]::Create($runner.Substring($start, $end - $start))
    function Write-TreeStep { param($StepTag, $Title) }
    function Next-StepTag { '1/3' }
    function Write-TreeItem { param($Message, $State) Write-Host $Message }

    $processName = 'VNotchRunnerProbe_' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $debugDirectory = Join-Path $temporary 'bin-debug'
    $installedDirectory = Join-Path $temporary 'installed-copy'
    New-Item -ItemType Directory -Path $debugDirectory, $installedDirectory | Out-Null
    $exePath = Join-Path $debugDirectory ($processName + '.exe')
    $installedExe = Join-Path $installedDirectory ($processName + '.exe')
    $source = Join-Path $temporary 'ProcessFixture.cs'
    Set-Content -LiteralPath $source -Encoding ASCII -Value 'class ProcessFixture { static void Main() { System.Threading.Thread.Sleep(-1); } }'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /target:winexe "/out:$exePath" $source
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile the dev runner process fixture.' }
    Copy-Item -LiteralPath $exePath -Destination $installedExe
    foreach ($path in @($exePath, $installedExe)) {
        $fixture = Start-Process -FilePath $path -WindowStyle Hidden -PassThru
        $fixtures += $fixture
        if ($fixture.WaitForExit(100)) { throw 'Dev runner process fixture exited before the check.' }
    }

    . $processCheck
    foreach ($fixture in $fixtures) {
        if (-not $fixture.WaitForExit(2000)) {
            throw 'r.ps1 left an old instance outside bin/Debug running.'
        }
    }
    # An already-stopped application is also a valid restart precondition.
    . $processCheck

    & {
        $currentSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
        $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $candidateProcesses = @(
            [pscustomobject]@{ Id = 101; SessionId = $currentSession },
            [pscustomobject]@{ Id = 102; SessionId = $currentSession },
            [pscustomobject]@{ Id = 103; SessionId = $currentSession + 1 }
        )
        function Get-Process {
            param($Name, $Id, $ErrorAction)
            if ($Id) { return $candidateProcesses | Where-Object Id -eq $Id }
            $candidateProcesses
        }
        function Get-CimInstance {
            param($ClassName, $Filter, $ErrorAction)
            [pscustomobject]@{ ProcessId = [int]($Filter -replace '^ProcessId = ', '') }
        }
        function Invoke-CimMethod {
            param($InputObject, $MethodName, $ErrorAction)
            $sid = if ($InputObject.ProcessId -eq 101) { $currentSid } else { 'S-1-5-18' }
            [pscustomobject]@{ ReturnValue = 0; Sid = $sid }
        }
        $selected = @(Get-VNotchInstances)
        if ($selected.Count -ne 1 -or $selected[0].Id -ne 101) {
            throw 'The runner selected an instance belonging to another user or Windows session.'
        }

        # Inspection errors must abort the restart instead of reporting no instance.
        function Invoke-CimMethod {
            param($InputObject, $MethodName, $ErrorAction)
            [pscustomobject]@{ ReturnValue = 2; Sid = $null }
        }
        $inspectionFailed = $false
        try { Get-VNotchInstances | Out-Null } catch {
            if ($_.Exception.Message -notlike 'Cannot inspect * PID *') { throw }
            $inspectionFailed = $true
        }
        if (-not $inspectionFailed) { throw 'The runner silently ignored a process ownership error.' }
    }
    Write-Host 'Dev runner checks passed: debug/installed copies stop; empty, user/session scope and inspection errors are handled.'
} finally {
    foreach ($fixture in $fixtures) {
        try {
            if (-not $fixture.HasExited) { Stop-Process -InputObject $fixture -Force -ErrorAction Stop }
        } finally { $fixture.Dispose() }
    }
    $resolved = [IO.Path]::GetFullPath($temporary)
    if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetTempPath().TrimEnd('\', '/') -or
        [IO.Path]::GetFileName($resolved) -notlike 'vnotch-dev-runner-*') { throw 'Unsafe dev runner test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
