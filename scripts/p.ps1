<#
.SYNOPSIS
    Automated Ponytail Pipeline (Audit -> Code -> Review -> Benchmark) for V-Notch.

.DESCRIPTION
    Takes a user requirement/prompt as input and orchestrates the 4-stage Ponytail workflow:
      1. AUDIT     (/ponytail-audit)  : Scan target scope for the requirement, rank findings, emit tasks in result/.
      2. CODE      (/ponytail)        : Implement minimal C++26 Native-First / C# facade changes + targeted tests.
      3. REVIEW    (/ponytail-review) : Review diffs against audit & requirement; loop until 0 Must/Should fix.
      4. BENCHMARK (Live + xUnit)     : Run Measure-AppPerformance.ps1 (if V-Notch is live) + 34 xUnit perf tests,
                                        and write a unified report to result/BENCHMARK_REPORT.md.

    Can run in three modes:
      - Headless CLI Agent mode (-Runner Auto / Claude / Agy)
      - Antigravity IDE Prompt & Handoff mode (-Runner PromptOnly, copies ready-to-run /goal prompt to clipboard)
      - Verification & Benchmark mode (-BenchmarkOnly, runs targeted tests + live telemetry + generates report)

.PARAMETER Prompt
    The feature request, bug description, optimization target, or audit scope to execute.

.PARAMETER Scope
    Optional directory or file path filter (relative to repo root) to focus the audit and coding stages.

.PARAMETER Runner
    Agent execution backend: 'Auto' (default), 'Claude', 'Agy', 'PromptOnly', or 'BenchmarkOnly'.

.PARAMETER MaxLoops
    Maximum Code <-> Review remediation iterations before moving to Benchmark (default: 2).

.PARAMETER TestFilter
    Targeted xUnit filter run after coding and during the benchmark stage.

.PARAMETER LiveBenchmarkSeconds
    Duration in seconds for live process telemetry via Measure-AppPerformance.ps1 (default: 10).

.PARAMETER WarmupSeconds
    Warmup duration in seconds before sampling live telemetry (default: 2).

.PARAMETER LaunchAppForBenchmark
    Starts V-Notch automatically before live benchmarking if no running instance is found.

.PARAMETER SkipLiveBenchmark
    Skips live process telemetry and runs only the xUnit microbenchmark suite.

.PARAMETER BenchmarkOnly
    Skips agent stages and directly runs Review Gate validation + Live/xUnit Benchmarks + Report generation.

.EXAMPLE
    .\scripts\p.ps1 "Tối ưu hiệu năng bộ nhớ và thread safety cho ClipboardModule"
.EXAMPLE
    .\scripts\p.ps1 -Prompt "Audit và fix các lỗi DWM flickering khi mở media popup" -Scope "Windows,Native"
.EXAMPLE
    .\scripts\p.ps1 -BenchmarkOnly -Prompt "Verify post-fix benchmark"
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Prompt = "",

    [string]$Scope = ".",

    [ValidateSet("Auto", "Claude", "Agy", "PromptOnly", "BenchmarkOnly")]
    [string]$Runner = "Auto",

    [ValidateRange(1, 5)]
    [int]$MaxLoops = 2,

    [string]$TestFilter = "FullyQualifiedName~PerformanceBenchmarkTests|FullyQualifiedName~ScrubberPerformanceTests|FullyQualifiedName~ClipboardTrayOpeningPerformanceTests|FullyQualifiedName~ProgressViewModelPerformanceTests|FullyQualifiedName~PerformanceAuditRegressionTests",

    [ValidateRange(3, 300)]
    [int]$LiveBenchmarkSeconds = 10,

    [ValidateRange(0, 60)]
    [int]$WarmupSeconds = 2,

    [switch]$LaunchAppForBenchmark,

    [switch]$SkipLiveBenchmark,

    [switch]$BenchmarkOnly
)

Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSVersion.Major -ge 7) {
    $PSNativeCommandUseErrorActionPreference = $false
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Invoke-CapturedNative([scriptblock]$Command) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $Command 2>&1
    } finally {
        $ErrorActionPreference = $prev
    }
}

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$repoRoot  = Split-Path $scriptDir -Parent
$resultDir = Join-Path $repoRoot "result"
$promptDir = Join-Path $resultDir "prompts"

if ($BenchmarkOnly) {
    $Runner = "BenchmarkOnly"
}
if (-not $Prompt -and $Runner -ne "BenchmarkOnly") {
    throw "Please provide a requirement via -Prompt `"<yêu cầu>`" (or use -BenchmarkOnly)."
}
if (-not $Prompt) {
    $Prompt = "General Ponytail Audit, Remediation, Review & Benchmark"
}

# --- Runtime Unicode Glyphs (7-bit ASCII source safe for PS 5.1 & PS 7) ----
$gTopLeft = [char]0x256D # ╭
$gBotLeft = [char]0x2570 # ╰
$gVert    = [char]0x2502 # │
$gBranch  = [char]0x251C # ├
$gHoriz   = [char]0x2500 # ─
$gDiamond = [char]0x25C6 # ◆
$gCheck   = [char]0x2714 # ✔
$gCross   = [char]0x2716 # ✖
$gWarn    = [char]0x25B2 # ▲
$gArrow   = [char]0x25B8 # ▸
$lineRule = ([string]$gHoriz) * 62

function Write-TreeStep([string]$Tag, [string]$Title) {
    Write-Host "  $gBranch$gHoriz " -ForegroundColor DarkCyan -NoNewline
    Write-Host "[$Tag] " -ForegroundColor Cyan -NoNewline
    Write-Host $Title -ForegroundColor White
}

function Write-TreeItem([string]$Message, [string]$State = "Info") {
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

function Resolve-AgentCli([string]$Preferred) {
    if ($Preferred -eq "BenchmarkOnly" -or $Preferred -eq "PromptOnly") {
        return $Preferred
    }
    if ($Preferred -eq "Claude" -or $Preferred -eq "Auto") {
        $claudeCmd = Get-Command claude.exe -ErrorAction SilentlyContinue
        if (-not $claudeCmd) { $claudeCmd = Get-Command claude -ErrorAction SilentlyContinue }
        if ($claudeCmd) { return "Claude" }
        if ($Preferred -eq "Claude") { throw "Claude CLI not found in PATH." }
    }
    if ($Preferred -eq "Agy" -or $Preferred -eq "Auto") {
        $agyCmd = Get-Command agy.exe -ErrorAction SilentlyContinue
        if (-not $agyCmd) { $agyCmd = Get-Command agy -ErrorAction SilentlyContinue }
        if ($agyCmd) { return "Agy" }
        if ($Preferred -eq "Agy") { throw "Antigravity CLI (agy) not found in PATH." }
    }
    return "PromptOnly"
}

function Get-ReviewGateStatus([string]$ReviewFilePath) {
    if (-not (Test-Path -LiteralPath $ReviewFilePath)) {
        return [pscustomobject]@{
            Exists      = $false
            Passed      = $false
            MustFix     = -1
            ShouldFix   = -1
            Consider    = -1
            VerdictLine = "Missing PONYTAIL_REVIEW.md"
        }
    }
    $raw = Get-Content -LiteralPath $ReviewFilePath -Raw -Encoding UTF8
    $mustFix = 0
    $shouldFix = 0
    $consider = 0

    if ($raw -match '(?i)\*\*(\d+)\s+Must\s+fix\*\*') {
        $mustFix = [int]$Matches[1]
    }
    if ($raw -match '(?i)\*\*(\d+)\s+Should\s+fix\*\*') {
        $shouldFix = [int]$Matches[1]
    }
    if ($raw -match '(?i)\*\*(\d+)\s+Consider\*\*') {
        $consider = [int]$Matches[1]
    }

    $hasShipVerdict = $raw -match '(?i)(Verdict:\s*[^\r\n]*Ship|0\s+Must\s+fix[^\r\n]*0\s+Should\s+fix)'
    $passed = ($mustFix -eq 0 -and $shouldFix -eq 0 -and $hasShipVerdict)

    return [pscustomobject]@{
        Exists      = $true
        Passed      = $passed
        MustFix     = $mustFix
        ShouldFix   = $shouldFix
        Consider    = $consider
        VerdictLine = "$mustFix Must fix, $shouldFix Should fix, $consider Consider"
    }
}

function Invoke-StageAgent([string]$Backend, [string]$StageName, [string]$PromptFilePath) {
    $stagePrompt = Get-Content -LiteralPath $PromptFilePath -Raw -Encoding UTF8
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    if ($Backend -eq "Claude") {
        Write-TreeItem "Running stage '$StageName' via Claude CLI..." -State "Info"
        $out = Invoke-CapturedNative { & claude -p $stagePrompt --dangerously-skip-permissions }
        $exit = $LASTEXITCODE
        $sw.Stop()
        if ($exit -ne 0) {
            Write-TreeItem ("Stage '$StageName' exited with code $exit ({0:N1}s)." -f $sw.Elapsed.TotalSeconds) -State "Warn"
            return $false
        }
        Write-TreeItem ("Stage '$StageName' completed in {0:N1}s." -f $sw.Elapsed.TotalSeconds) -State "Success"
        return $true
    }
    if ($Backend -eq "Agy") {
        Write-TreeItem "Running stage '$StageName' via Antigravity CLI (agy)..." -State "Info"
        $out = Invoke-CapturedNative { & agy -p $stagePrompt }
        $exit = $LASTEXITCODE
        $sw.Stop()
        if ($exit -ne 0) {
            Write-TreeItem ("Stage '$StageName' exited with code $exit ({0:N1}s)." -f $sw.Elapsed.TotalSeconds) -State "Warn"
            return $false
        }
        Write-TreeItem ("Stage '$StageName' completed in {0:N1}s." -f $sw.Elapsed.TotalSeconds) -State "Success"
        return $true
    }
    return $false
}

$totalSw = [System.Diagnostics.Stopwatch]::StartNew()
$resolvedBackend = Resolve-AgentCli -Preferred $Runner

Write-Host ""
Write-Host "  $gTopLeft$lineRule" -ForegroundColor DarkCyan
Write-Host "  $gVert  " -ForegroundColor DarkCyan -NoNewline
Write-Host "$gDiamond V-NOTCH PONYTAIL PIPELINE " -ForegroundColor Cyan -NoNewline
Write-Host "[$resolvedBackend]" -ForegroundColor Magenta
Write-Host "  $gVert  " -ForegroundColor DarkCyan -NoNewline
Write-Host "Request : " -ForegroundColor DarkGray -NoNewline
$shortPrompt = if ($Prompt.Length -gt 52) { $Prompt.Substring(0, 49) + "..." } else { $Prompt }
Write-Host $shortPrompt -ForegroundColor White
Write-Host "  $gVert  " -ForegroundColor DarkCyan -NoNewline
Write-Host "Scope   : " -ForegroundColor DarkGray -NoNewline
Write-Host "$Scope (MaxLoops: $MaxLoops)" -ForegroundColor Gray
Write-Host "  $gVert" -ForegroundColor DarkCyan

Push-Location $repoRoot
try {
    if (-not (Test-Path -LiteralPath $resultDir)) {
        New-Item -ItemType Directory -Path $resultDir -Force | Out-Null
    }
    if (-not (Test-Path -LiteralPath $promptDir)) {
        New-Item -ItemType Directory -Path $promptDir -Force | Out-Null
    }

    # --- STEP 1: Generate Request Manifest & Stage Prompts -------------------
    Write-TreeStep "1/4" "Prepare Ponytail Handoff & Stage Prompts"
    $timestampUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

    $requestManifestPath = Join-Path $resultDir "PIPELINE_REQUEST.md"
    $auditPromptPath     = Join-Path $promptDir "01-ponytail-audit.md"
    $codePromptPath      = Join-Path $promptDir "02-ponytail-code.md"
    $reviewPromptPath    = Join-Path $promptDir "03-ponytail-review.md"
    $chatOrchestratorPath = Join-Path $promptDir "00-antigravity-chat-command.md"

    $bt = '`'
    $requestManifest = @"
# Ponytail Pipeline Request

- **Timestamp (UTC)**: $bt$timestampUtc$bt
- **User Requirement**: $Prompt
- **Target Scope**: $bt$Scope$bt
- **Max Remediation Loops**: $bt$MaxLoops$bt
- **Targeted Test Filter**: $bt$TestFilter$bt

## Mandatory Architecture Constraints
1. **C++26 Native-First (${bt}GEMINI.md${bt})**: All new business logic, algorithms, parsers, scoring, state calculations, or Win32/OS calls MUST live in ${bt}Native/${bt} (${bt}VNotch.Native.dll${bt}) with strict ${bt}extern "C" __declspec(dllexport) noexcept${bt} ABI and Caller-Allocates ${bt}Span<T>${bt}. Keep C# strictly for WPF UI and ${bt}[LibraryImport("VNotch.Native")]${bt} facades.
2. **Ponytail Philosophy (${bt}ponytail${bt})**: Smallest change that completely solves the problem. Guard clauses, flat loops, zero speculative abstractions.
3. **Artifacts in ${bt}result/${bt}**:
   - Audit Report: ${bt}result/PONYTAIL_AUDIT.md${bt}
   - Remediation Tasks: ${bt}result/AGENT_REMEDIATION_TASKS.md${bt}
   - Implementation Log: ${bt}result/FIX_RESULTS.md${bt}
   - Quality Review: ${bt}result/PONYTAIL_REVIEW.md${bt}
   - Benchmark Report: ${bt}result/BENCHMARK_REPORT.md${bt}
"@
    Set-Content -LiteralPath $requestManifestPath -Value $requestManifest -Encoding UTF8

    $auditPrompt = @"
/ponytail-audit
Thực hiện audit cho yêu cầu sau trong repo ${bt}d:\CodeShii\V-Notch${bt}:
- Yêu cầu: "$Prompt"
- Phạm vi ưu tiên (Scope): $bt$Scope$bt

Hướng dẫn bắt buộc:
1. Đọc skill ${bt}C:\Users\gauze\.gemini\config\skills\ponytail-audit\SKILL.md${bt}.
2. Quét đúng phạm vi liên quan đến yêu cầu (ưu tiên ${bt}codegraph_explore${bt} và ${bt}ctx_execute${bt} theo ${bt}GEMINI.md${bt}).
3. Phân loại các vấn đề/cơ hội tối ưu theo mức độ nghiêm trọng (${bt}Critical${bt}, ${bt}High${bt}, ${bt}Medium${bt}, ${bt}Low${bt}) và giải thích bằng ngôn ngữ ngắn gọn, dễ hiểu.
4. Ghi kết quả chi tiết vào ${bt}d:\CodeShii\V-Notch\result\PONYTAIL_AUDIT.md${bt} và danh sách task cụ thể (${bt}T-01${bt}, ${bt}T-02${bt}, ...) vào ${bt}d:\CodeShii\V-Notch\result\AGENT_REMEDIATION_TASKS.md${bt}.
"@
    Set-Content -LiteralPath $auditPromptPath -Value $auditPrompt -Encoding UTF8

    $codePrompt = @"
/ponytail
Thực hiện code/fix theo yêu cầu và danh sách task trong ${bt}d:\CodeShii\V-Notch\result\AGENT_REMEDIATION_TASKS.md${bt} (và ${bt}d:\CodeShii\V-Notch\result\PONYTAIL_REVIEW.md${bt} nếu có finding cần sửa lại):
- Yêu cầu gốc: "$Prompt"
- Phạm vi: $bt$Scope$bt

Hướng dẫn bắt buộc:
1. Đọc skill ${bt}C:\Users\gauze\.gemini\config\skills\ponytail\SKILL.md${bt} và tuân thủ tuyệt đối kiến trúc **C++26 Native-First** trong ${bt}GEMINI.md${bt}.
2. Thực hiện giải pháp gọn nhất giải quyết triệt để vấn đề (early return, không tạo class/abstraction thừa).
3. Chạy targeted test (${bt}dotnet test Tests\VNotch.Tests.csproj --filter "$TestFilter"${bt} hoặc test của module vừa sửa; thêm ${bt}--no-build${bt} nếu ${bt}V-Notch.exe${bt} đang chạy khóa file).
4. Cập nhật kết quả từng task vào ${bt}d:\CodeShii\V-Notch\result\FIX_RESULTS.md${bt}.
"@
    Set-Content -LiteralPath $codePromptPath -Value $codePrompt -Encoding UTF8

    $reviewPrompt = @"
/ponytail-review
Thực hiện review toàn bộ các thay đổi vừa code dựa trên ${bt}d:\CodeShii\V-Notch\result\PONYTAIL_AUDIT.md${bt}, ${bt}d:\CodeShii\V-Notch\result\AGENT_REMEDIATION_TASKS.md${bt}, và ${bt}d:\CodeShii\V-Notch\result\FIX_RESULTS.md${bt}:
- Yêu cầu gốc: "$Prompt"

Hướng dẫn bắt buộc:
1. Đọc skill ${bt}C:\Users\gauze\.gemini\config\skills\ponytail-review\SKILL.md${bt}.
2. Kiểm tra kỹ từng file đã sửa (logic, memory safety C++26/P-Invoke, race condition, edge cases, test coverage, và xem có dòng code nào thừa không).
3. Ghi báo cáo vào ${bt}d:\CodeShii\V-Notch\result\PONYTAIL_REVIEW.md${bt} kèm dòng tổng kết định dạng chuẩn:
   ${bt}**X Must fix**, **Y Should fix**, **Z Consider**${bt} và ${bt}Verdict: Ship${bt} (nếu X=0, Y=0) hoặc ${bt}Verdict: Needs fixes${bt}.
"@
    Set-Content -LiteralPath $reviewPromptPath -Value $reviewPrompt -Encoding UTF8

    $chatOrchestratorPrompt = @"
/goal /ponytail-pipeline $Prompt
"@
    Set-Content -LiteralPath $chatOrchestratorPath -Value $chatOrchestratorPrompt -Encoding UTF8

    Write-TreeItem "Saved request manifest -> result\PIPELINE_REQUEST.md" -State "Success"
    Write-TreeItem "Generated stage prompts -> result\prompts\01..03-*.md" -State "Success"

    # --- STEP 2 & 3: Audit -> Code <-> Review Loop ---------------------------
    Write-TreeSpacer
    Write-TreeStep "2/4" "Execute Audit -> Code -> Review Pipeline"

    if ($resolvedBackend -eq "BenchmarkOnly") {
        Write-TreeItem "Mode is BenchmarkOnly: skipping agent invocation stages." -State "Info"
    } elseif ($resolvedBackend -eq "PromptOnly") {
        try {
            Set-Clipboard -Value $chatOrchestratorPrompt.Trim()
            Write-TreeItem "Copied '/goal /ponytail-pipeline ...' command to Clipboard!" -State "Success"
        } catch {
            Write-TreeItem "Could not copy to clipboard automatically." -State "Warn"
        }
        Write-TreeItem "Paste into Antigravity Chat to run multi-agent Audit -> Code -> Review," -State "Info"
        Write-TreeItem "or pass -Runner Claude if running headless from terminal." -State "Info"
    } else {
        # Stage 1: Audit
        $auditOk = Invoke-StageAgent -Backend $resolvedBackend -StageName "1. Ponytail Audit" -PromptFilePath $auditPromptPath
        if (-not $auditOk) {
            Write-TreeItem "Audit stage reported a non-zero exit; continuing to check artifacts..." -State "Warn"
        }

        # Stage 2 & 3: Code <-> Review Loop
        for ($loop = 1; $loop -le $MaxLoops; $loop++) {
            Write-TreeItem "Iteration $loop/$MaxLoops : Running Code/Fix stage (/ponytail)..." -State "Info"
            [void](Invoke-StageAgent -Backend $resolvedBackend -StageName "2.$loop Ponytail Code" -PromptFilePath $codePromptPath)

            Write-TreeItem "Iteration $loop/$MaxLoops : Running Quality Review stage (/ponytail-review)..." -State "Info"
            [void](Invoke-StageAgent -Backend $resolvedBackend -StageName "3.$loop Ponytail Review" -PromptFilePath $reviewPromptPath)

            $gate = Get-ReviewGateStatus -ReviewFilePath (Join-Path $resultDir "PONYTAIL_REVIEW.md")
            if ($gate.Passed) {
                Write-TreeItem "Review Gate PASSED on iteration $loop ($($gate.VerdictLine))." -State "Success"
                break
            } else {
                Write-TreeItem "Review Gate status after iteration $loop : $($gate.VerdictLine)" -State "Warn"
            }
        }
    }

    # Inspect current Review Gate status
    Write-TreeSpacer
    Write-TreeStep "3/4" "Verify Review Gate & Run Targeted Microbenchmarks"
    $reviewPath = Join-Path $resultDir "PONYTAIL_REVIEW.md"
    $reviewGate = Get-ReviewGateStatus -ReviewFilePath $reviewPath
    if ($reviewGate.Exists) {
        $gateState = if ($reviewGate.Passed) { "Success" } else { "Warn" }
        Write-TreeItem "Review Gate (result\PONYTAIL_REVIEW.md): $($reviewGate.VerdictLine)" -State $gateState
    } else {
        Write-TreeItem "result\PONYTAIL_REVIEW.md not found yet." -State "Warn"
    }

    # Check if V-Notch.exe is running (to avoid MSB3027 file lock during dotnet test)
    $runningProc = @(Get-Process -Name "V-Notch" -ErrorAction SilentlyContinue)
    $isAppRunning = ($runningProc.Count -gt 0)
    $testArgs = @("test", "Tests\VNotch.Tests.csproj", "--filter", $TestFilter, "--logger", "console;verbosity=normal")
    if ($isAppRunning) {
        Write-TreeItem "V-Notch is running (PID $($runningProc[0].Id)) -> using --no-build to avoid EXE lock." -State "Info"
        $testArgs += "--no-build"
    }

    $testSw = [System.Diagnostics.Stopwatch]::StartNew()
    $testRaw = Invoke-CapturedNative { & dotnet @testArgs }
    $testExit = $LASTEXITCODE
    $testSw.Stop()

    $testLines = @($testRaw | ForEach-Object { "$_" })
    $summaryLine = $testLines | Where-Object { $_ -match "(Passed!|Failed!)" } | Select-Object -Last 1
    $perfEntries = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $testLines) {
        if ($line -match "^\s*Passed\s+([^\s\[]+)\s+\[([^\]]+)\]") {
            $shortTestName = ($Matches[1] -split '\.')[-1]
            $perfEntries.Add("| ${bt}$shortTestName${bt} | ${bt}$($Matches[2])${bt} | PASS |")
        } elseif ($line -match "^\s*Failed\s+([^\s\[]+)") {
            $shortTestName = ($Matches[1] -split '\.')[-1]
            $perfEntries.Add("| ${bt}$shortTestName${bt} | - | FAIL |")
        }
    }

    if ($testExit -eq 0) {
        $msg = if ($summaryLine) { $summaryLine.Trim() } else { "All targeted benchmark tests passed in {0:N2}s." -f $testSw.Elapsed.TotalSeconds }
        Write-TreeItem $msg -State "Success"
    } else {
        Write-TreeItem ("Targeted tests exited with code $testExit ({0:N2}s)." -f $testSw.Elapsed.TotalSeconds) -State "Error"
    }

    # --- STEP 4: Live Process Telemetry & Unified Report ---------------------
    Write-TreeSpacer
    Write-TreeStep "4/4" "Capture Live Telemetry & Write result\BENCHMARK_REPORT.md"

    $liveJsonPath = Join-Path $resultDir "live-app-benchmark.json"
    $liveSummaryMd = "_Skipped or V-Notch was not running during benchmark._"
    $spawnedTempApp = $false
    $targetPid = $null

    if (-not $SkipLiveBenchmark) {
        $runningProc = @(Get-Process -Name "V-Notch" -ErrorAction SilentlyContinue)
        if ($runningProc.Count -eq 0 -and $LaunchAppForBenchmark) {
            $exeCandidate = Join-Path $repoRoot "bin\Debug\net8.0-windows10.0.19041.0\win-x64\V-Notch.exe"
            if (Test-Path -LiteralPath $exeCandidate) {
                Write-TreeItem "Starting V-Notch temporarily for live benchmark..." -State "Info"
                $p = Start-Process -FilePath $exeCandidate -WorkingDirectory (Split-Path $exeCandidate -Parent) -PassThru
                $targetPid = $p.Id
                $spawnedTempApp = $true
                Start-Sleep -Seconds 2
            }
        } elseif ($runningProc.Count -gt 0) {
            $targetPid = $runningProc[0].Id
        }

        if ($targetPid) {
            Write-TreeItem "Sampling live process V-Notch (PID $targetPid) for ${LiveBenchmarkSeconds}s (warmup ${WarmupSeconds}s)..." -State "Info"
            $measureScript = Join-Path $scriptDir "Measure-AppPerformance.ps1"
            try {
                $procSnap = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
                $handleCount = if ($procSnap) { $procSnap.HandleCount } else { 0 }
                $threadCount = if ($procSnap) { $procSnap.Threads.Count } else { 0 }
                & $measureScript -AppProcessId $targetPid -DurationSeconds $LiveBenchmarkSeconds -WarmupSeconds $WarmupSeconds -SampleIntervalSeconds 1 -OutputPath $liveJsonPath *>&1 | Out-Null
                if (Test-Path -LiteralPath $liveJsonPath) {
                    $liveData = Get-Content -LiteralPath $liveJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
                    $s = $liveData.Summary
                    $cpuMachMean = [math]::Round([double]$s.CpuPercentOfMachine.Mean, 2)
                    $cpuMachP95  = [math]::Round([double]$s.CpuPercentOfMachine.P95, 2)
                    $cpuMachMax  = [math]::Round([double]$s.CpuPercentOfMachine.Max, 2)
                    $cpuCoreMean = [math]::Round([double]$s.CpuPercentOfOneCore.Mean, 2)
                    $cpuCoreP95  = [math]::Round([double]$s.CpuPercentOfOneCore.P95, 2)
                    $cpuCoreMax  = [math]::Round([double]$s.CpuPercentOfOneCore.Max, 2)
                    $wsMeanMb    = [math]::Round([double]$s.WorkingSetBytes.Mean / 1MB, 2)
                    $wsP95Mb     = [math]::Round([double]$s.WorkingSetBytes.P95 / 1MB, 2)
                    $wsMaxMb     = [math]::Round([double]$s.WorkingSetBytes.Max / 1MB, 2)
                    $privMeanMb  = [math]::Round([double]$s.PrivateBytes.Mean / 1MB, 2)
                    $privP95Mb   = [math]::Round([double]$s.PrivateBytes.P95 / 1MB, 2)
                    $privMaxMb   = [math]::Round([double]$s.PrivateBytes.Max / 1MB, 2)

                    $gpu3dMean = 0.0
                    $gpu3dMax  = 0.0
                    if ($s.GpuEngines) {
                        foreach ($eng in $s.GpuEngines) {
                            if ($eng.UtilizationPercent.Mean -gt $gpu3dMean) { $gpu3dMean = [math]::Round([double]$eng.UtilizationPercent.Mean, 2) }
                            if ($eng.UtilizationPercent.Max -gt $gpu3dMax)   { $gpu3dMax  = [math]::Round([double]$eng.UtilizationPercent.Max, 2) }
                        }
                    }

                    Write-TreeItem ("Live CPU: avg {0}% (max {1}%) | RAM WorkingSet: {2} MB | Private: {3} MB | Handles: {4} | Threads: {5}" -f `
                        $cpuMachMean, $cpuMachMax, $wsMeanMb, $privMeanMb, $handleCount, $threadCount) -State "Success"

                    $liveSummaryMd = @"
- **Process**: ${bt}V-Notch (PID $targetPid)${bt} | **Samples**: ${bt}$($s.SampleCount)${bt} | **Handles**: ${bt}$handleCount${bt} | **Threads**: ${bt}$threadCount${bt}

| Metric | Mean | P95 | Max |
| :--- | :---: | :---: | :---: |
| **CPU (% Total Machine)** | ${bt}${cpuMachMean}%${bt} | ${bt}${cpuMachP95}%${bt} | ${bt}${cpuMachMax}%${bt} |
| **CPU (% Single Core)** | ${bt}${cpuCoreMean}%${bt} | ${bt}${cpuCoreP95}%${bt} | ${bt}${cpuCoreMax}%${bt} |
| **Working Set RAM (MB)** | ${bt}${wsMeanMb} MB${bt} | ${bt}${wsP95Mb} MB${bt} | ${bt}${wsMaxMb} MB${bt} |
| **Private Memory (MB)** | ${bt}${privMeanMb} MB${bt} | ${bt}${privP95Mb} MB${bt} | ${bt}${privMaxMb} MB${bt} |
| **Peak GPU Engine (%)** | ${bt}${gpu3dMean}%${bt} | - | ${bt}${gpu3dMax}%${bt} |
"@
                }
            } catch {
                Write-TreeItem "Live telemetry capture failed: $($_.Exception.Message)" -State "Warn"
            } finally {
                if (Test-Path -LiteralPath $liveJsonPath) {
                    Remove-Item -LiteralPath $liveJsonPath -Force -ErrorAction SilentlyContinue
                }
                if ($spawnedTempApp -and $targetPid) {
                    Stop-Process -Id $targetPid -Force -ErrorAction SilentlyContinue
                }
            }
        } else {
            Write-TreeItem "V-Notch is not currently running (use -LaunchAppForBenchmark or start .\scripts\r.ps1 for live telemetry)." -State "Info"
        }
    }

    $perfTableRows = if ($perfEntries.Count -gt 0) {
        ($perfEntries -join "`r`n")
    } else {
        "| _No matching test entries parsed_ | - | - |"
    }

    $testElapsedSec = [math]::Round($testSw.Elapsed.TotalSeconds, 2)
    $cleanSummaryLine = if ($summaryLine) { $summaryLine.Trim() } else { "Completed ($($perfEntries.Count) benchmark tests)" }
    $gatePassedStr = if ($reviewGate.Passed) { "true (Ship)" } else { "false" }

    $benchmarkReportPath = Join-Path $resultDir "BENCHMARK_REPORT.md"
    $reportMd = @"
# Ponytail Pipeline & Benchmark Report

- **Generated (UTC)**: ${bt}$timestampUtc${bt}
- **Requirement**: $Prompt
- **Scope**: ${bt}$Scope${bt}
- **Review Gate**: $($reviewGate.VerdictLine) (Passed: ${bt}$gatePassedStr${bt})
- **xUnit Benchmark Exit Code**: ${bt}$testExit${bt} (${bt}${testElapsedSec}s${bt})

---

## 1. Live Process Telemetry (${bt}V-Notch.exe${bt})

$liveSummaryMd

---

## 2. Targeted xUnit Performance & Regression Microbenchmarks

Summary: ${bt}$cleanSummaryLine${bt}

| Benchmark / Regression Test | Duration | Status |
| :--- | :---: | :---: |
$perfTableRows
"@
    Set-Content -LiteralPath $benchmarkReportPath -Value $reportMd -Encoding UTF8
    Write-TreeItem "Wrote unified benchmark report -> result\BENCHMARK_REPORT.md" -State "Success"

    $totalSw.Stop()
    Write-TreeSpacer
    Write-Host "  $gBotLeft$lineRule" -ForegroundColor DarkCyan
    Write-Host "     $gCheck PONYTAIL PIPELINE READY " -ForegroundColor Green -NoNewline
    Write-Host ("-> result\BENCHMARK_REPORT.md ({0:N1}s)" -f $totalSw.Elapsed.TotalSeconds) -ForegroundColor Gray
    Write-Host ""
} finally {
    $testResultsLeftover = Join-Path $repoRoot "Tests\TestResults"
    if (Test-Path -LiteralPath $testResultsLeftover) {
        Remove-Item -LiteralPath $testResultsLeftover -Recurse -Force -ErrorAction SilentlyContinue
    }
    Pop-Location
}
