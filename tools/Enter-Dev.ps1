# Dot-source this file to use the isolated SDK and dependency caches.
param([string]$LabRoot = 'D:\CCleanLab\CCLEAN-20260917-DEV')
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath "$LabRoot\dotnet\dotnet.exe")) {
    throw "Install the SDK pinned by global.json in $LabRoot\dotnet first."
}
if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($LabRoot)).AvailableFreeSpace -lt 10GB) {
    throw 'The development volume must retain at least 10 GiB.'
}
$env:DOTNET_ROOT = "$LabRoot\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_HOME = "$LabRoot\cli"
$env:NUGET_PACKAGES = "$LabRoot\nuget\packages"
$env:NUGET_HTTP_CACHE_PATH = "$LabRoot\nuget\http"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
