[CmdletBinding()]
param(
    [string]$ArtifactsRoot = (Join-Path $PSScriptRoot '../artifacts/distribution'),
    [string]$DotNet = 'dotnet',
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$ArtifactsRoot = [IO.Path]::GetFullPath($ArtifactsRoot)
$publish = Join-Path $ArtifactsRoot 'publish'
if (Test-Path $publish) { throw 'Publish directory already exists. Choose a fresh ArtifactsRoot to avoid stale files.' }
New-Item -ItemType Directory -Force $ArtifactsRoot | Out-Null
$build = Join-Path $ArtifactsRoot 'build'
Push-Location $repo
try {
    & $Python -m unittest discover -s tests -v
    if ($LASTEXITCODE -ne 0) { throw 'Packaging tests failed.' }
    & $DotNet restore TjClient.slnx --locked-mode "-p:ArtifactsPath=$build"
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & $DotNet build TjClient.slnx -c Release --no-restore "-p:ArtifactsPath=$build"
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    $result = & $DotNet run --project TjClient.Tests -c Release --no-build --no-restore "-p:ArtifactsPath=$build"
    $testExit = $LASTEXITCODE
    $result | Tee-Object -FilePath (Join-Path $ArtifactsRoot 'native-tests.log') | Write-Host
    $summaries = @($result | Where-Object { $_ -match '^RESULT total=[1-9][0-9]* failed=0$' })
    if ($testExit -ne 0 -or $summaries.Count -ne 1) { throw 'The complete native suite must pass with a nonzero test count.' }
    & $DotNet publish TjClient.Wpf -c Release -r win-x64 --self-contained true --no-restore "-p:ArtifactsPath=$build" -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
    foreach ($name in @('LICENSE', 'THIRD_PARTY_NOTICES.md', 'licenses')) {
        Copy-Item -LiteralPath (Join-Path $repo $name) -Destination (Join-Path $publish $name) -Recurse
    }
    & (Join-Path $PSScriptRoot 'Test-Smoke.ps1') -ExePath (Join-Path $publish 'TjCloudClient.exe')
    Write-Host "Build and native smoke passed: $publish"
}
finally { Pop-Location }
