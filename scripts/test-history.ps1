param([Parameter(Mandatory=$true)][string]$ProtectedOpenAiKey)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$data = Join-Path $root ('artifacts\live-history-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data | Out-Null
$keyCopies = @((Join-Path $data 'openai-key.protected'))
$process = $null
try {
    Copy-Item -LiteralPath $ProtectedOpenAiKey -Destination $keyCopies[0]

    $exe = Join-Path $root 'dist\single-file\SecondBrain.exe'
    $process = Start-Process -FilePath $exe -ArgumentList @('--data-dir', ('"' + $data + '"'), '--smoke-test', 'history-live') -PassThru -WindowStyle Hidden
    while (-not $process.WaitForExit(1000)) { if ((Get-Date) - $process.StartTime -gt [TimeSpan]::FromSeconds(150)) { throw 'Live history test timed out.' } }
    $report = Get-Content -Raw -LiteralPath (Join-Path $data 'history-live.json') | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or -not $report.passed) { throw "Live test failed. Inspect $data" }
    Write-Output 'PASS real structured Markdown generation and selective undo on synthetic notes; no physical capture'
    Write-Output "Evidence: $data"
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    foreach ($copy in $keyCopies) { if (Test-Path -LiteralPath $copy) { Remove-Item -LiteralPath $copy } }
}
