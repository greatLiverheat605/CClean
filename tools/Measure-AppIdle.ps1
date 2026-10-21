param(
    [Parameter(Mandatory)][int]$AppProcessId,
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][string]$Output,
    [int]$Seconds = 600
)
$ErrorActionPreference = 'Stop'
if ($Seconds -lt 600) { throw 'Idle acceptance requires at least ten minutes.' }
$samples = [Collections.Generic.List[object]]::new()
$app = Get-Process -Id $AppProcessId
$startCpu = $app.TotalProcessorTime.TotalSeconds
$timer = [Diagnostics.Stopwatch]::StartNew()
while ($timer.Elapsed.TotalSeconds -lt $Seconds) {
    Start-Sleep -Seconds 5
    $app.Refresh()
    if ($app.HasExited) { throw 'Application exited during idle measurement.' }
    $dataBytes = (Get-ChildItem -LiteralPath $DataDirectory -File | Measure-Object Length -Sum).Sum
    $samples.Add([pscustomobject]@{ Seconds=$timer.Elapsed.TotalSeconds; WorkingSet=$app.WorkingSet64; PrivateBytes=$app.PrivateMemorySize64; DataBytes=$dataBytes })
}
$cpu = ($app.TotalProcessorTime.TotalSeconds - $startCpu) / $timer.Elapsed.TotalSeconds / [Environment]::ProcessorCount * 100
$report = [pscustomobject]@{
    StartedAt=(Get-Date).AddSeconds(-$timer.Elapsed.TotalSeconds).ToString('o')
    DurationSeconds=$timer.Elapsed.TotalSeconds
    LogicalProcessors=[Environment]::ProcessorCount
    MachineCpuPercent=$cpu
    PeakSampleWorkingSet=($samples | Measure-Object WorkingSet -Maximum).Maximum
    PeakSampleDataBytes=($samples | Measure-Object DataBytes -Maximum).Maximum
    CpuPassed=($cpu -le 1)
    MemoryPassed=(($samples | Measure-Object WorkingSet -Maximum).Maximum -le 200MB)
    Samples=$samples
}
$fullOutput=[IO.Path]::GetFullPath($Output)
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
if (!$fullOutput.StartsWith($artifactRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Output must remain in artifacts.' }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullOutput)) | Out-Null
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $fullOutput -Encoding utf8
$report | Select-Object DurationSeconds,MachineCpuPercent,PeakSampleWorkingSet,PeakSampleDataBytes,CpuPassed,MemoryPassed
if (!$report.CpuPassed -or !$report.MemoryPassed) { exit 1 }
