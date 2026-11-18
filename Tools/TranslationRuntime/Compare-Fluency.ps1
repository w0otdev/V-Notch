param(
    [Parameter(Mandatory=$true)][string]$ModelDirectory,
    [Parameter(Mandatory=$true)][string]$RuntimeDll,
    [Parameter(Mandatory=$true)][string]$OutputJson,
    [int]$Threads = 4,
    [switch]$Fast
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Test-Runtime.ps1') -ModelDirectory $ModelDirectory -RuntimeDll $RuntimeDll -Threads $Threads -Fast:$Fast -DefinitionsOnly
$cases = @(
    @{text='Save your changes.';target='vi'},
    @{text='The restaurant is open every day.';target='vi'},
    @{text="Could you give me a hand with this? I'm running out of time.";target='vi'},
    @{text="I'll get back to you as soon as I hear from the team.";target='vi'},
    @{text="That sounds good to me. Let's call it a day and pick this up tomorrow.";target='vi'},
    @{text='Please bear with us while we sort this out.';target='vi'},
    @{text="She turned down the offer because it wasn't what she was looking for.";target='vi'},
    @{text='Dự án này vẫn còn vài chỗ chưa ổn, nhưng nhìn chung tôi thấy hướng đi khá tốt.';target='en'},
    @{text='Bạn cứ thong thả, khi nào xong thì gửi mình nhé.';target='en'},
    @{text="Could you let me know when you're free? I'd love to catch up.";target='de'},
    @{text='Je passerai te prendre vers huit heures, si ça te convient.';target='en'},
    @{text='不用着急，准备好了再告诉我。';target='en'},
    @{text='お手数ですが、もう一度確認していただけますか。';target='en'}
)
[LiteRuntimeProbe]::Load((Resolve-Path -LiteralPath $RuntimeDll).Path, $false)
if ([LiteRuntimeProbe]::Start((Resolve-Path -LiteralPath $ModelDirectory).Path, $Threads) -ne 0) { throw 'Model load failed' }
if ($Fast) { [LiteRuntimeProbe]::Natural($false) }
try {
    $results = foreach ($case in $cases) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $translated = [LiteRuntimeProbe]::Run($case.text, $case.target, -1, 65536, 0)
        $item = [pscustomobject]@{source=$case.text;target=$case.target;output=$translated;ms=[math]::Round($timer.Elapsed.TotalMilliseconds,1)}
        Write-Host ($item | ConvertTo-Json -Compress)
        $item
    }
    # Human-reviewed fixtures, not an automatic linguistic-quality score.
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputJson -Encoding utf8
} finally { [LiteRuntimeProbe]::Stop() }
