[CmdletBinding()]
param(
    [string]$OutputPath = '',
    [string]$VcVars64Path = '',
    [string]$BaselineCommit = 'HEAD',
    [switch]$Verify,
    [switch]$HotPathsOnly,
    [string]$PreviousNativeDll = ''
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ge 7) { $PSNativeCommandUseErrorActionPreference = $false }
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputPath) { $OutputPath = Join-Path $repo ('artifacts\native-benchmark-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputPath)
if ((Test-Path -LiteralPath (Join-Path $output 'results.json')) -or (Test-Path -LiteralPath (Join-Path $output 'hotpath-results.json'))) { throw 'Output already contains results; choose a new directory to preserve its snapshots.' }
if ($HotPathsOnly -and (-not $PreviousNativeDll -or -not (Test-Path -LiteralPath $PreviousNativeDll))) { throw 'Hot-path mode needs -PreviousNativeDll pointing to the saved pre-optimization DLL.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
if (-not $VcVars64Path) { $VcVars64Path = [IO.File]::ReadAllText((Join-Path $repo 'obj\native\vcvars64.cache')).Trim() }
if (-not (Test-Path -LiteralPath $VcVars64Path)) { throw 'Visual C++ environment script not found.' }
$packageRoot = Join-Path $env:USERPROFILE '.nuget\packages'
$ortRoot = Join-Path $packageRoot 'microsoft.ml.onnxruntime.directml\1.24.4'
$dmlPath = Join-Path $packageRoot 'microsoft.ai.directml\1.15.4\bin\x64-win\DirectML.dll'
if (-not (Test-Path -LiteralPath $dmlPath)) { throw "DirectML runtime not found: $dmlPath" }
$runtime = Join-Path $output 'runtime'
$nativeOut = Join-Path $output 'native'
[IO.Directory]::CreateDirectory($runtime) | Out-Null
[IO.Directory]::CreateDirectory($nativeOut) | Out-Null
Copy-Item -LiteralPath (Join-Path $ortRoot 'runtimes\win-x64\native\onnxruntime.dll') -Destination $runtime
Copy-Item -LiteralPath $dmlPath -Destination $runtime

$commit = (& git -C $repo rev-parse --verify "$BaselineCommit^{commit}").Trim()
if ($LASTEXITCODE -ne 0) { throw 'Baseline commit could not be resolved.' }
$archive = Join-Path $output 'baseline.zip'
& git -C $repo archive --format=zip "--output=$archive" $commit Native
if ($LASTEXITCODE -ne 0) { throw 'Could not snapshot HEAD.' }
$baselineRoot = Join-Path $output 'baseline'
Expand-Archive -LiteralPath $archive -DestinationPath $baselineRoot -Force
$currentRoot = Join-Path $output 'current'
$currentNative = Join-Path $currentRoot 'Native'
[IO.Directory]::CreateDirectory($currentNative) | Out-Null
$sourceNative = Join-Path $repo 'Native'
foreach ($header in Get-ChildItem -LiteralPath $sourceNative -Filter '*.h' -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($sourceNative, $header.FullName)
    $destination = Join-Path $currentNative $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $header.FullName -Destination $destination
}
$commonSources = @(
    'Services\CpuFeatures.cpp', 'Services\VisualizerDsp.cpp', 'Services\VisualizerSpectrumBands.cpp',
    'Services\VisualizerFftKernel.avx2.cpp', 'Services\DynamicIslandColorExtractor.cpp',
    'Services\SensitiveDataScrubber.cpp', 'Services\Clipboard\ClipboardSensitiveData.cpp',
    'Services\SmartThumbnailCropService.cpp'
)
$newSources = @(
    'Services\SmartCropInference.cpp', 'Services\SmartCropPreprocess.cpp', 'Services\SmartCropResize.avx2.cpp',
    'Services\Oklab.avx2.cpp', 'Services\VisualizerStream.cpp', 'Services\Clipboard\ClipboardRecordLog.cpp'
)
foreach ($relative in ($commonSources + $newSources)) {
    $destination = Join-Path $currentNative $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceNative $relative) -Destination $destination
}

function Build-Native([string]$Name, [string]$NativeRoot, [string[]]$Sources) {
    $obj = Join-Path $output ("obj-" + $Name)
    [IO.Directory]::CreateDirectory($obj) | Out-Null
    $baselineRsp = Join-Path $obj 'baseline.rsp'
    $avxRsp = Join-Path $obj 'avx.rsp'
    $linkRsp = Join-Path $obj 'link.rsp'
    [IO.File]::WriteAllLines($baselineRsp, @($Sources | Where-Object { $_ -notlike '*.avx2.cpp' } | ForEach-Object { '"' + (Join-Path $NativeRoot $_) + '"' }))
    [IO.File]::WriteAllLines($avxRsp, @($Sources | Where-Object { $_ -like '*.avx2.cpp' } | ForEach-Object { '"' + (Join-Path $NativeRoot $_) + '"' }))
    [IO.File]::WriteAllLines($linkRsp, @($Sources | ForEach-Object { '"' + (Join-Path $obj ([IO.Path]::GetFileNameWithoutExtension($_) + '.obj')) + '"' }))
    $dll = Join-Path $nativeOut ($Name + '.dll')
    $include = Join-Path $ortRoot 'build\native\include'
    $cmdPath = Join-Path $obj 'build.cmd'
    $commands = @(
        '@echo off',
        ('call "' + $VcVars64Path + '" >nul'),
        'if errorlevel 1 exit /b 1',
        ('cl.exe /nologo /c /std:c++latest /O2 /Oi /Ot /GL /MT /EHsc /MP /W4 /utf-8 /I"' + $NativeRoot + '" /I"' + $include + '" /Fo"' + $obj + '\\" @"' + $baselineRsp + '"'),
        'if errorlevel 1 exit /b 1',
        ('cl.exe /nologo /c /std:c++latest /O2 /Oi /Ot /arch:AVX2 /MT /EHsc /MP /W4 /utf-8 /I"' + $NativeRoot + '" /Fo"' + $obj + '\\" @"' + $avxRsp + '"'),
        'if errorlevel 1 exit /b 1',
        ('link.exe /nologo /DLL /LTCG /OPT:REF /OPT:ICF /MACHINE:X64 /OUT:"' + $dll + '" /IMPLIB:"' + (Join-Path $obj ($Name + '.lib')) + '" @"' + $linkRsp + '"'),
        'exit /b %errorlevel%'
    )
    [IO.File]::WriteAllLines($cmdPath, $commands)
    Write-Host "Building $Name from isolated source snapshot..."
    & cmd.exe /d /c $cmdPath *> (Join-Path $output ("build-" + $Name + '.log'))
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $output ("build-" + $Name + '.log')) -Tail 30; throw "$Name native compilation failed" }
}
Build-Native 'baseline' (Join-Path $baselineRoot 'Native') $commonSources
Build-Native 'current' $currentNative ($commonSources + $newSources)

$sourceHashes = @($commonSources + $newSources | ForEach-Object {
    [pscustomobject]@{Path=$_; Sha256=(Get-FileHash -LiteralPath (Join-Path $currentNative $_) -Algorithm SHA256).Hash}
})
$modelsRoot = Join-Path $env:LOCALAPPDATA 'V-Notch\translation-models'
$localModels = if (Test-Path -LiteralPath $modelsRoot) {
    @(Get-ChildItem -LiteralPath $modelsRoot -Filter '*.gguf' -File -Recurse | Select-Object FullName,Length)
} else { @() }
$metadata = [pscustomobject]@{
    CapturedAtUtc = [DateTime]::UtcNow.ToString('o')
    BaselineCommit = $commit
    Cpu = @(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
    Os = @(Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber)
    Computer = @(Get-CimInstance Win32_ComputerSystem | Select-Object TotalPhysicalMemory)
    Gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion)
    PowerScheme = (& powercfg /getactivescheme | Out-String).Trim()
    Build = 'MSVC /std:c++latest /O2 /GL /MT; AVX2 objects /arch:AVX2 without /GL; /LTCG /OPT:REF /OPT:ICF; .NET8 Release, tiered compilation disabled'
    VcVars = $VcVars64Path
    BaselineDllSha256 = (Get-FileHash -LiteralPath (Join-Path $nativeOut 'baseline.dll')).Hash
    CurrentDllSha256 = (Get-FileHash -LiteralPath (Join-Path $nativeOut 'current.dll')).Hash
    OrtDllSha256 = (Get-FileHash -LiteralPath (Join-Path $runtime 'onnxruntime.dll')).Hash
    DirectMlDllSha256 = (Get-FileHash -LiteralPath (Join-Path $runtime 'DirectML.dll')).Hash
    ModelSha256 = (Get-FileHash -LiteralPath (Join-Path $repo 'Models\yolox_nano.onnx')).Hash
    CurrentSources = $sourceHashes
    LocalTranslationModels = $localModels
    Scope = 'Isolated native snapshots and synthetic documents; no running app restarted and no actual clipboard history accessed.'
}
[IO.File]::WriteAllText((Join-Path $output 'environment.json'), ($metadata | ConvertTo-Json -Depth 7))
$tool = Join-Path $repo 'Tools\NativePipelineBench\NativePipelineBench.csproj'
$harnessSources = Join-Path $output 'harness-source'
[IO.Directory]::CreateDirectory($harnessSources) | Out-Null
Get-ChildItem -LiteralPath (Split-Path $tool -Parent) -File | Copy-Item -Destination $harnessSources
$toolOutput = Join-Path $output 'harness'
& dotnet build $tool -c Release -o $toolOutput --verbosity quiet *> (Join-Path $output 'build-harness.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $output 'build-harness.log') -Tail 30; throw 'Benchmark harness compilation failed' }
$env:DOTNET_TieredCompilation = '0'
$harness = Join-Path $toolOutput 'NativePipelineBench.dll'
if ($HotPathsOnly) {
    $previousCopy = Join-Path $nativeOut 'previous.dll'
    Copy-Item -LiteralPath ([IO.Path]::GetFullPath($PreviousNativeDll)) -Destination $previousCopy
    & dotnet $harness hotpaths $repo $output $runtime $previousCopy
    if ($LASTEXITCODE -ne 0) { throw 'Hot-path benchmark failed' }
    if ($Verify) {
        & dotnet $harness hotpaths $repo $output $runtime $previousCopy repeat
        if ($LASTEXITCODE -ne 0) { throw 'Hot-path benchmark verification failed' }
    }
    & python (Join-Path $repo 'Tools\NativePipelineBench\report.py') $output
    if ($LASTEXITCODE -ne 0) { throw 'Hot-path report generation failed' }
    Write-Host "Hot-path benchmark artifacts: $output"
    return
}
for ($round = 0; $round -lt 3; ++$round) {
    $backends = @('managed-cpu','native-cpu','directml')
    for ($i = 0; $i -lt 3; ++$i) {
        $backend = $backends[($round + $i) % 3]
        $coldOutput = Join-Path $output ("cold-$backend-$round.json")
        & dotnet $harness cold $repo $output $runtime $backend $coldOutput
        if ($LASTEXITCODE -ne 0) { throw "Cold benchmark failed: $backend" }
    }
}
& dotnet $harness run $repo $output $runtime
if ($LASTEXITCODE -ne 0) { throw 'Benchmark run failed' }
if ($Verify) {
    & dotnet $harness verify $repo $output $runtime
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark verification failed' }
}
& python (Join-Path $repo 'Tools\NativePipelineBench\report.py') $output
if ($LASTEXITCODE -ne 0) { throw 'Benchmark report generation failed' }
Write-Host "Benchmark artifacts: $output"
