param([Parameter(Mandatory=$true)][string]$ProtectedOpenAiKey, [Parameter(Mandatory=$true)][string]$ProtectedDeepgramKey)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$data = Join-Path $root ('artifacts\live-companion-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data | Out-Null
$keyCopies = @((Join-Path $data 'openai-key.protected'), (Join-Path $data 'deepgram-key.protected'))
$process = $null
try {
    Copy-Item -LiteralPath $ProtectedOpenAiKey -Destination $keyCopies[0]
    Copy-Item -LiteralPath $ProtectedDeepgramKey -Destination $keyCopies[1]
    $exe = Join-Path $root 'dist\single-file\SecondBrain.exe'
    $process = Start-Process -FilePath $exe -ArgumentList @('--data-dir', ('"' + $data + '"'), '--smoke-test', 'companion-live') -PassThru -WindowStyle Hidden
    while (-not $process.WaitForExit(1000)) { if ((Get-Date) - $process.StartTime -gt [TimeSpan]::FromSeconds(110)) { throw 'Live companion test timed out.' } }
    $report = Get-Content -Raw -LiteralPath (Join-Path $data 'companion-live.json') | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or -not $report.passed) { throw "Live test failed. Inspect $data" }
    Write-Output 'PASS generated system speech through Deepgram and OpenAI to reader; no physical capture'
    Write-Output "Evidence: $data"
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    foreach ($copy in $keyCopies) { if (Test-Path -LiteralPath $copy) { Remove-Item -LiteralPath $copy } }
}
