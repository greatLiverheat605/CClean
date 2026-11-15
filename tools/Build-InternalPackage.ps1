param(
    [Parameter(Mandatory)][string]$Iscc,
    [switch]$SnapshotPreview
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $compilerVersion = & $Iscc --version
    if (($compilerVersion -join "`n") -notmatch '7\.1\.0') { throw 'Pinned Inno Setup 7.1.0 required.' }
    $commit = git rev-parse HEAD
    $dirty = [bool](git status --porcelain)
    if ($dirty -and -not $SnapshotPreview) { throw 'Commit reviewed changes, or use -SnapshotPreview for a hashed, unaccepted local preview.' }
    [xml]$project = Get-Content src/CClean.App/CClean.App.csproj -Raw -Encoding utf8
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.-]+)?$') { throw 'Invalid application version.' }
    if ($SnapshotPreview -and $version -notmatch '-preview\.') { throw 'Snapshot packages require an explicit preview version.' }
    $run = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfff') + '-' + [Guid]::NewGuid().ToString('N')
    $buildRoot = Join-Path $projectRoot "artifacts/package-builds/$run"
    $sourceRoot = Join-Path $buildRoot 'source'
    $publishRoot = Join-Path $buildRoot 'publish'
    $packageRoot = Join-Path $projectRoot 'artifacts/packages'
    New-Item -ItemType Directory -Path $sourceRoot,$packageRoot -Force | Out-Null
    # Both tracked and non-ignored new files are part of this immutable local input set.
    $paths = ((& git -c core.quotepath=false ls-files -z --cached --others --exclude-standard) -join "`n").Split([char]0) |
        Where-Object { $_ -ne '' } | Sort-Object -Unique
    if ($LASTEXITCODE -ne 0 -or $paths.Count -eq 0) { throw 'Cannot enumerate build inputs.' }
    $inputs = foreach ($relative in $paths) {
        $original = Join-Path $projectRoot $relative
        # Deleted tracked files are intentionally absent from the snapshot.
        if (-not (Test-Path -LiteralPath $original -PathType Leaf)) { continue }
        $target = Join-Path $sourceRoot $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        $before = (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash
        Copy-Item -LiteralPath $original -Destination $target
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $before -or
            (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -ne $before) { throw "Input changed while freezing: $relative" }
        [ordered]@{ path=$relative; sha256=$before }
    }
    $inputFile = Join-Path $buildRoot 'source-files.json'
    ConvertTo-Json -InputObject @($inputs) -Depth 4 | Set-Content -LiteralPath $inputFile -Encoding utf8
    $sourceHash = (Get-FileHash -LiteralPath $inputFile -Algorithm SHA256).Hash
    dotnet publish (Join-Path $sourceRoot 'src/CClean.App') -c Release -r win-x64 --self-contained true -o $publishRoot
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $provenance = [ordered]@{ version=$version; commit=$commit; workingTreeDirty=$dirty; snapshotPreview=[bool]$SnapshotPreview; sourceManifestSha256=$sourceHash; builtAt=[DateTimeOffset]::UtcNow.ToString('O'); sdk=(& dotnet --version); innoSetup='7.1.0'; releaseAcceptance='pending'; signed=$false }
    $provenance | ConvertTo-Json | Set-Content (Join-Path $publishRoot 'build-provenance.json') -Encoding utf8
    Copy-Item -LiteralPath $inputFile -Destination (Join-Path $publishRoot 'source-files.json')
    & $Iscc "/DAppVersion=$version" "/DPublishDir=$publishRoot" "/O$packageRoot" (Join-Path $sourceRoot 'packaging/CClean.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    foreach ($entry in $inputs) {
        if ((Get-FileHash -LiteralPath (Join-Path $sourceRoot $entry.path) -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Frozen source changed during packaging: $($entry.path)"
        }
    }
    $installer = Join-Path $packageRoot "CClean-$version-internal-win-x64.exe"
    $provenance.installerSha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    $provenance | ConvertTo-Json | Set-Content (Join-Path $packageRoot "CClean-$version-build.json") -Encoding utf8
    Get-ChildItem artifacts/packages/*.exe | Get-FileHash -Algorithm SHA256 | Select-Object @{Name='File';Expression={Split-Path $_.Path -Leaf}},Hash | ConvertTo-Json | Set-Content artifacts/packages/SHA256.json -Encoding utf8
    Write-Output $installer
} finally { Pop-Location }
