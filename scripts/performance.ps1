param([switch]$Probe)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$data = Join-Path $root ('artifacts\performance-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data | Out-Null
$exe = Join-Path $data 'SecondBrain.exe'
Copy-Item -LiteralPath (Join-Path $root 'dist\single-file\SecondBrain.exe') -Destination $exe
$phase = if ($Probe) { 'performance30' } else { 'performance' }
$duration = if ($Probe) { 30 } else { 1200 }
@{ startedUtc=[DateTime]::UtcNow.ToString('o'); executableHash=(Get-FileHash -LiteralPath $exe).Hash;
    durationSeconds=$duration; display=(Get-CimInstance Win32_VideoController | Select-Object Name,CurrentRefreshRate) } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $data 'environment.json')
$process = Start-Process -FilePath $exe -ArgumentList @('--data-dir',('"'+$data+'"'),'--smoke-test',$phase) -PassThru -WindowStyle Hidden
$deadline = [DateTime]::UtcNow.AddSeconds($duration + 180)
try {
    while (-not $process.WaitForExit(1000)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Performance test timed out. Inspect $data" }
    }
    $report = Get-Content -Raw -LiteralPath (Join-Path $data 'performance.json') | ConvertFrom-Json
    $report | ConvertTo-Json
    if ($process.ExitCode -ne 0 -or -not $report.passed) { throw "Performance criteria failed. Inspect $data" }
    Write-Output "Evidence: $data"
} finally {
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
}
