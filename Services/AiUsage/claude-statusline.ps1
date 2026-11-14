# V-Notch Claude Code status-line bridge. No tokens, prompts, paths or transcripts are saved.
# The only stored values are provider-reported quota percentages, resets, and observation time.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try {
    $buffer = New-Object char[] 4096
    $inputText = New-Object System.Text.StringBuilder
    while (($read = [Console]::In.Read($buffer, 0, $buffer.Length)) -gt 0) {
        if ($inputText.Length + $read -gt 262144) { exit 0 }
        [void]$inputText.Append($buffer, 0, $read)
    }
    $payload = $inputText.ToString() | ConvertFrom-Json
    $limits = [ordered]@{}
    $display = New-Object System.Collections.Generic.List[string]
    foreach ($entry in @(@('five_hour', '5h'), @('seven_day', '7d'))) {
        $value = $payload.rate_limits.($entry[0])
        if ($null -eq $value -or $null -eq $value.used_percentage) { continue }
        # Reject strings and non-finite/out-of-range values rather than guessing.
        if ($value.used_percentage -is [string] -or $value.used_percentage -is [bool]) { continue }
        $percent = [double]$value.used_percentage
        if ([double]::IsNaN($percent) -or [double]::IsInfinity($percent) -or $percent -lt 0 -or $percent -gt 100) { continue }
        $reset = $null
        if ($null -ne $value.resets_at) {
            if ($value.resets_at -is [string] -or $value.resets_at -is [bool]) { continue }
            $reset = [long]$value.resets_at
            if ($reset -ne $value.resets_at) { continue }
            [void][DateTimeOffset]::FromUnixTimeSeconds($reset)
        }
        $limits[$entry[0]] = [ordered]@{ used_percentage = $percent; resets_at = $reset }
        $display.Add(('{0}: {1:0.#}% left' -f $entry[1], (100 - $percent)))
    }
    # Missing fields explicitly replace the old sample; switching to API billing must not retain old plan bars.
    $record = [ordered]@{ observed_at = [DateTimeOffset]::UtcNow.ToString('o'); rate_limits = $limits }
    $directory = Join-Path $env:LOCALAPPDATA 'V-Notch/ai-usage'
    for ($check = $directory; $check; $check = [IO.Path]::GetDirectoryName($check)) {
        if ((Test-Path -LiteralPath $check) -and ((Get-Item -LiteralPath $check -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { exit 0 }
    }
    [void][IO.Directory]::CreateDirectory($directory)
    $target = Join-Path $directory 'claude.json'
    if ((Test-Path -LiteralPath $target) -and ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { exit 0 }
    $temp = Join-Path $directory ([Guid]::NewGuid().ToString('N') + '.tmp')
    $backup = $temp + '.bak'
    try {
        [IO.File]::WriteAllText($temp, ($record | ConvertTo-Json -Depth 4 -Compress), (New-Object Text.UTF8Encoding $false))
        # File.Replace is atomic on Windows, including with overlapping Claude sessions.
        if ([IO.File]::Exists($target)) { [IO.File]::Replace($temp, $target, $backup) }
        else {
            try { [IO.File]::Move($temp, $target) }
            catch [IO.IOException] { if ([IO.File]::Exists($target)) { [IO.File]::Replace($temp, $target, $backup) } else { throw } }
        }
    } finally {
        if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
        if ([IO.File]::Exists($backup)) { [IO.File]::Delete($backup) }
    }
    if ($display.Count -gt 0) { [Console]::Write('Claude ' + ($display -join ' | ')) }
} catch {
    # Status lines must not break Claude or print private input/exception details.
    exit 0
}
