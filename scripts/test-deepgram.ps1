param([Parameter(Mandatory=$true)][string]$ProtectedKeyPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$data = Join-Path $root ('artifacts\live-deepgram-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data | Out-Null
$keyCopy = Join-Path $data 'deepgram-key.protected'
$process = $null
try {
    Copy-Item -LiteralPath $ProtectedKeyPath -Destination $keyCopy
    $exe = Join-Path $root 'dist\single-file\SecondBrain.exe'
    $process = Start-Process -FilePath $exe -ArgumentList @('--data-dir', ('"' + $data + '"'), '--smoke-test', 'deepgram') -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(30000)) { $process.Kill(); $process.WaitForExit(); throw "Live test timed out. Inspect $data" }
    $report = Get-Content -Raw -LiteralPath (Join-Path $data 'deepgram.json') | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or -not $report.passed) { throw "Live test failed. Inspect $data" }
    Write-Output 'PASS live Deepgram transcription of synthetic WAV; microphone never opened'
    Write-Output "Evidence: $data"
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    if (Test-Path -LiteralPath $keyCopy) { Remove-Item -LiteralPath $keyCopy }
}
