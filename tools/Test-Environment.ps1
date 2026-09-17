. "$PSScriptRoot/Enter-Dev.ps1"
dotnet build "$PSScriptRoot/../CClean.slnx" -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$run = [guid]::NewGuid().ToString('N')
dotnet test "$PSScriptRoot/../tests/CClean.Tests" -c Release --no-build --filter Category=Environment --logger "trx;LogFileName=environment-$run.trx"
if ($LASTEXITCODE -ne 0) { throw 'Environment tests failed' }
$trx = [xml](Get-Content -LiteralPath "$PSScriptRoot/../tests/CClean.Tests/TestResults/environment-$run.trx" -Raw)
if ([int]$trx.TestRun.ResultSummary.Counters.executed -le 0) { throw 'No tests selected' }
$cases = @(
    @{ Name = 'environment'; Sandbox = 'artifacts/m0/sandbox'; Expected = 0; Extra = @() },
    @{ Name = 'unknown'; Sandbox = 'artifacts/m0/sandbox'; Expected = 2; Extra = @() },
    @{ Name = 'environment'; Sandbox = 'docs'; Expected = 3; Extra = @() },
    @{ Name = 'environment'; Sandbox = 'artifacts/../docs'; Expected = 3; Extra = @() },
    @{ Name = 'environment'; Sandbox = 'artifacts/m0/sandbox'; Expected = 2; Extra = @('--max-fixture-bytes', '9223372036854775807') },
    @{ Name = 'environment'; Sandbox = 'artifacts/m0/sandbox'; Expected = 2; Extra = @('--file-count', '0') }
)
foreach ($case in $cases) {
    $arguments = @('run', '--project', 'tools/CClean.Poc', '-c', 'Release', '--no-build', '--', '--case', $case.Name, '--sandbox', $case.Sandbox, '--output', 'artifacts/m0/environment-check.json') + $case.Extra
    & dotnet @arguments
    if ($LASTEXITCODE -ne $case.Expected) { throw "Unexpected exit code for $($case.Name) / $($case.Sandbox): $LASTEXITCODE" }
}
Write-Output "Environment validation passed: $($trx.TestRun.ResultSummary.Counters.executed) tests, $($cases.Count) CLI scenarios."
