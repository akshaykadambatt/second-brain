param([string]$Executable = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $root 'dist\win-x64\SecondBrain.exe' }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$runtime = Split-Path -Parent $Executable
foreach ($file in @('coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $runtime $file))) { throw "Self-contained runtime missing: $file" }
}
$data = Join-Path $root ('artifacts\smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $data | Out-Null
$oldRoot = $env:DOTNET_ROOT
$oldLookup = $env:DOTNET_MULTILEVEL_LOOKUP
try {
    $env:DOTNET_ROOT = Join-Path $data 'absent-runtime'
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    foreach ($phase in @('seed','verify')) {
        $p = Start-Process -FilePath $Executable -ArgumentList @('--data-dir', ('"' + $data + '"'), '--smoke-test', $phase) -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(30000)) { $p.Kill(); throw "Smoke phase timed out: $phase" }
        if ($p.ExitCode -ne 0) { throw "Smoke phase failed: $phase ($($p.ExitCode)). Inspect $data" }
        $report = Get-Content -Raw -LiteralPath (Join-Path $data "$phase.json") | ConvertFrom-Json
        if (-not $report.passed) { throw "Smoke assertions failed: $phase" }
        if (Get-Process -Id $p.Id -ErrorAction SilentlyContinue) { throw 'Application process survived exit.' }
        Write-Output "PASS packaged process: $phase; exited cleanly"
    }
    $report = @{ passed = $true; packaged = $true; processExited = $true; phases = @('seed','verify'); evidenceDirectory = (Split-Path -Leaf $data) }
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'artifacts\smoke-tests.json')
    Write-Output "Evidence: $data"
} finally {
    $env:DOTNET_ROOT = $oldRoot
    $env:DOTNET_MULTILEVEL_LOOKUP = $oldLookup
}
