param([string]$Executable = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $root 'dist\single-file\SecondBrain.exe' }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$data = Join-Path $root ('artifacts\smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $data | Out-Null
$isolatedExe = Join-Path $data 'SecondBrain.exe'
Copy-Item -LiteralPath $Executable -Destination $isolatedExe
$oldRoot = $env:DOTNET_ROOT
$oldLookup = $env:DOTNET_MULTILEVEL_LOOKUP
try {
    $env:DOTNET_ROOT = Join-Path $data 'absent-runtime'
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    foreach ($phase in @('seed','verify','voice','flow')) {
        $phaseData = if ($phase -in @('voice','flow')) { Join-Path $data $phase } else { $data }
        $p = Start-Process -FilePath $isolatedExe -ArgumentList @('--data-dir', ('"' + $phaseData + '"'), '--smoke-test', $phase) -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(30000)) { $p.Kill(); throw "Smoke phase timed out: $phase" }
        if ($p.ExitCode -ne 0) { throw "Smoke phase failed: $phase ($($p.ExitCode)). Inspect $data" }
        $report = Get-Content -Raw -LiteralPath (Join-Path $phaseData "$phase.json") | ConvertFrom-Json
        if (-not $report.passed) { throw "Smoke assertions failed: $phase" }
        if (Get-Process -Id $p.Id -ErrorAction SilentlyContinue) { throw 'Application process survived exit.' }
        Write-Output "PASS packaged process: $phase; exited cleanly"
    }
    $report = @{ passed = $true; singleExecutable = $true; processExited = $true; phases = @('seed','verify','voice','flow'); evidenceDirectory = (Split-Path -Leaf $data) }
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'artifacts\smoke-tests.json')
    Write-Output "Evidence: $data"
} finally {
    $env:DOTNET_ROOT = $oldRoot
    $env:DOTNET_MULTILEVEL_LOOKUP = $oldLookup
}
