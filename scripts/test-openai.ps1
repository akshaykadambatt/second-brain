param([Parameter(Mandatory=$true)][string]$ProtectedKeyPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$data = Join-Path $root ('artifacts\live-openai-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data | Out-Null
$keyCopy = Join-Path $data 'openai-key.protected'
$process = $null
try {
    Copy-Item -LiteralPath $ProtectedKeyPath -Destination $keyCopy
    $exe = Join-Path $root 'dist\single-file\SecondBrain.exe'
    $process = Start-Process -FilePath $exe -ArgumentList @('--data-dir', ('"' + $data + '"'), '--smoke-test', 'assistant-live') -PassThru -WindowStyle Hidden
    while (-not $process.WaitForExit(1000)) { if ((Get-Date) - $process.StartTime -gt [TimeSpan]::FromSeconds(200)) { throw 'Live AI test timed out.' } }
    $report = Get-Content -Raw -LiteralPath (Join-Path $data 'assistant-live.json') | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or -not $report.passed) { throw "Live test failed. Inspect $data" }
    Write-Output 'PASS live OpenAI quick and deeper answers with synthetic context; no audio capture'
    Write-Output "Evidence: $data"
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    if (Test-Path -LiteralPath $keyCopy) { Remove-Item -LiteralPath $keyCopy }
}
