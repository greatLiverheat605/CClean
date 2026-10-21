param(
    [Parameter(Mandatory)][int]$AppProcessId,
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][string]$Output,
    [ValidateRange(1, 604800)][int]$Seconds = 86400,
    [ValidateRange(1, 300)][int]$IntervalSeconds = 30,
    [string]$BuildCommit = ''
)
$ErrorActionPreference = 'Stop'
$fullOutput = [IO.Path]::GetFullPath($Output)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
if (!$fullOutput.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetExtension($fullOutput) -ne '.jsonl') { throw 'Output must be a .jsonl file inside artifacts.' }
$dataRoot = [IO.Path]::GetFullPath($DataDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (!(Test-Path -LiteralPath $dataRoot -PathType Container)) { throw 'Data directory does not exist.' }
if ($fullOutput.StartsWith($dataRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Observation output must remain outside the application data directory.'
}
$summaryPath = [IO.Path]::ChangeExtension($fullOutput, 'summary.json')
foreach ($destination in @($fullOutput, $summaryPath, ($summaryPath + '.new'))) {
    if (Test-Path -LiteralPath $destination) { throw 'Observation output already exists; choose a new run name.' }
}
$outputParent = [IO.Path]::GetDirectoryName($fullOutput)
for ($ancestor = $outputParent; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
    if ((Test-Path -LiteralPath $ancestor) -and
        ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Observation output cannot traverse a reparse point.'
    }
}
[IO.Directory]::CreateDirectory($outputParent) | Out-Null
$writer = [IO.StreamWriter]::new([IO.FileStream]::new($fullOutput, [IO.FileMode]::CreateNew,
    [IO.FileAccess]::Write, [IO.FileShare]::Read), [Text.UTF8Encoding]::new($false))
$writer.AutoFlush = $true
function Write-Record([hashtable]$Record) {
    $Record.Utc = [DateTimeOffset]::UtcNow.ToString('o')
    $writer.WriteLine(($Record | ConvertTo-Json -Depth 6 -Compress))
}
$startedAt = [DateTimeOffset]::UtcNow
$timer = [Diagnostics.Stopwatch]::StartNew()
$nonAcceptance = $Seconds -lt 86400
$status = 'running'
$failure = $null
$samples = 0
$gaps = 0
$peakWorkingSet = 0L
$peakPrivateBytes = 0L
$peakDataBytes = 0L
$firstSample = $null
$lastSample = $null
$identityTicks = $null
$executableSha256 = $null
$startCpu = $null
try {
    $initial = Get-Process -Id $AppProcessId
    try {
        $identityTicks = $initial.StartTime.ToUniversalTime().Ticks
        $executableSha256 = (Get-FileHash -LiteralPath $initial.Path -Algorithm SHA256).Hash
        $startCpu = $initial.TotalProcessorTime.TotalSeconds
    }
    finally { $initial.Dispose() }
    Write-Record @{ Type = 'started'; ProcessId = $AppProcessId; ProcessStartUtcTicks = $identityTicks;
        ExecutableSha256 = $executableSha256; BuildCommit = $BuildCommit; RequestedSeconds = $Seconds;
        IntervalSeconds = $IntervalSeconds; NonAcceptance = $nonAcceptance }
    while ($true) {
        $elapsed = $timer.Elapsed.TotalSeconds
        if ($null -ne $lastSample -and $elapsed - $lastSample.ElapsedSeconds -gt [Math]::Max(90, 3 * $IntervalSeconds)) {
            $gaps++
            Write-Record @{ Type = 'observation_gap'; FromSeconds = $lastSample.ElapsedSeconds; ToSeconds = $elapsed;
                Reason = 'Sampling was interrupted or the host was suspended; this interval is not verified.' }
        }
        $current = Get-Process -Id $AppProcessId -ErrorAction SilentlyContinue
        if ($null -eq $current) {
            $status = 'process_exited'
            Write-Record @{ Type = $status; ElapsedSeconds = $elapsed }
            break
        }
        try {
            if ($current.StartTime.ToUniversalTime().Ticks -ne $identityTicks) {
                $status = 'process_identity_changed'
                Write-Record @{ Type = $status; ElapsedSeconds = $elapsed }
                break
            }
            $dataBytes = (Get-ChildItem -LiteralPath $dataRoot -File -Force | Measure-Object Length -Sum).Sum
            $sample = @{ Type = 'sample'; ElapsedSeconds = $elapsed; WorkingSet = $current.WorkingSet64;
                PrivateBytes = $current.PrivateMemorySize64; CpuSeconds = $current.TotalProcessorTime.TotalSeconds;
                DataBytes = [long]$dataBytes }
            Write-Record $sample
            $samples++
            $peakWorkingSet = [Math]::Max($peakWorkingSet, $sample.WorkingSet)
            $peakPrivateBytes = [Math]::Max($peakPrivateBytes, $sample.PrivateBytes)
            $peakDataBytes = [Math]::Max($peakDataBytes, $sample.DataBytes)
            if ($null -eq $firstSample) { $firstSample = $sample }
            $lastSample = $sample
        }
        finally { $current.Dispose() }
        if ($timer.Elapsed.TotalSeconds -ge $Seconds) { $status = 'completed'; break }
        Start-Sleep -Milliseconds ([int][Math]::Max(1, [Math]::Min($IntervalSeconds * 1000, ($Seconds - $timer.Elapsed.TotalSeconds) * 1000)))
    }
}
catch {
    $status = 'failed'
    $failure = $_.Exception.Message
    Write-Record @{ Type = 'failed'; ElapsedSeconds = $timer.Elapsed.TotalSeconds; Error = $failure }
}
finally {
    $timer.Stop()
    if ($status -eq 'running') { $status = 'interrupted' }
    $duration = $timer.Elapsed.TotalSeconds
    $summary = [pscustomobject][ordered]@{
        StartedAt = $startedAt.ToString('o'); EndedAt = [DateTimeOffset]::UtcNow.ToString('o')
        Status = $status; Error = $failure; ProcessId = $AppProcessId; ProcessStartUtcTicks = $identityTicks
        ExecutableSha256 = $executableSha256; BuildCommit = $BuildCommit; RequestedSeconds = $Seconds
        DurationSeconds = $duration; IntervalSeconds = $IntervalSeconds; SampleCount = $samples; ObservationGaps = $gaps
        NonAcceptance = $nonAcceptance; Meets24HourObservation = (!$nonAcceptance -and $status -eq 'completed' -and $gaps -eq 0 -and $duration -ge 86400)
        MultiDayAcceptance = 'not_assessed'; WorkingSetTargetBytes = 200MB
        WorkingSetTargetPassed = ($samples -gt 0 -and $peakWorkingSet -le 200MB)
        PeakWorkingSet = $peakWorkingSet; PeakPrivateBytes = $peakPrivateBytes; PeakDataBytes = $peakDataBytes
        FirstSample = $firstSample; LastSample = $lastSample
        MachineAverageCpuPercent = $(if ($null -ne $lastSample -and $lastSample.ElapsedSeconds -gt 0) {
            ($lastSample.CpuSeconds - $startCpu) / $lastSample.ElapsedSeconds / [Environment]::ProcessorCount * 100
        } else { $null })
        Note = 'Continuous observation only; does not replace idle CPU acceptance or prove absence of memory leaks. Hard termination may leave only the flushed JSONL prefix.'
    }
    try {
        Write-Record @{ Type = 'finished'; Status = $status; SampleCount = $samples; ObservationGaps = $gaps; NonAcceptance = $nonAcceptance }
    }
    finally {
        $writer.Dispose()
        $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath ($summaryPath + '.new') -Encoding utf8
        Move-Item -LiteralPath ($summaryPath + '.new') -Destination $summaryPath
    }
}
$summary | Select-Object Status, DurationSeconds, SampleCount, ObservationGaps, NonAcceptance, Meets24HourObservation, PeakWorkingSet
if ($status -ne 'completed') { exit 1 }
