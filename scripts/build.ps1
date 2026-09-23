param(
    [string]$Dotnet = 'dotnet',
    [string]$PackageSource = '',
    [switch]$SkipPackage
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$environmentKeys = @('DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'APPDATA', 'LOCALAPPDATA')
$savedEnvironment = @{}
foreach ($key in $environmentKeys) { $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
Push-Location $root
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $env:DOTNET_CLI_HOME = Join-Path $root '.build\cli'
    $env:NUGET_PACKAGES = Join-Path $root '.build\packages'
    # Isolate the build's user-level NuGet/cache discovery from personal settings.
    $env:APPDATA = Join-Path $root '.build\profile\Roaming'
    $env:LOCALAPPDATA = Join-Path $root '.build\profile\Local'
    New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA | Out-Null
    $restoreArgs = @('-p:RestoreConfigFile=' + (Join-Path $root 'NuGet.Config'))
    if ($PackageSource) { $restoreArgs += @('--source', $PackageSource) }
    & $Dotnet restore SecondBrain.slnx @restoreArgs -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & $Dotnet build SecondBrain.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $Dotnet run --project tests\SecondBrain.Tests -c Release --no-build -- $root
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    if (-not $SkipPackage) {
        & $Dotnet publish src\SecondBrain.App -c Release -r win-x64 --self-contained true -o dist\win-x64 @restoreArgs -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw 'Package failed.' }
    }
} finally {
    foreach ($key in $environmentKeys) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process') }
    Pop-Location
}
