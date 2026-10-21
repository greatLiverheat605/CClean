param([Parameter(Mandatory)][string]$Iscc)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $compilerVersion = & $Iscc --version
    if (($compilerVersion -join "`n") -notmatch '7\.1\.0') { throw 'Pinned Inno Setup 7.1.0 required.' }
    $commit = git rev-parse HEAD
    $dirty = [bool](git status --porcelain)
    if ($dirty) { throw 'Commit reviewed changes before building a traceable internal package.' }
    dotnet publish src/CClean.App -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $provenance = @{ commit=$commit; workingTreeDirty=$dirty; builtAt=[DateTimeOffset]::UtcNow.ToString('O'); sdk=(& dotnet --version); innoSetup='7.1.0'; releaseAcceptance='pending'; signed=$false }
    $provenance | ConvertTo-Json | Set-Content artifacts/publish/win-x64/build-provenance.json -Encoding utf8
    if ((git rev-parse HEAD) -ne $commit -or (git status --porcelain)) { throw 'Source changed during publish; rebuild from a clean commit.' }
    & $Iscc packaging/CClean.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    if ((git rev-parse HEAD) -ne $commit -or (git status --porcelain)) { throw 'Source changed during packaging; discard this package and rebuild.' }
    Get-ChildItem artifacts/packages/*.exe | Get-FileHash -Algorithm SHA256 | Select-Object @{Name='File';Expression={Split-Path $_.Path -Leaf}},Hash | ConvertTo-Json | Set-Content artifacts/packages/SHA256.json -Encoding utf8
} finally { Pop-Location }
