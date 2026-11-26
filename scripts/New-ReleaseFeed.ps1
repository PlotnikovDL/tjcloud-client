[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$GeneratorPath,
    [Parameter(Mandatory)][string]$VerifierDll,
    [string]$DotNet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or
    $env:RUNNER_OS -ne 'Windows' -or $env:GITHUB_EVENT_NAME -ne 'push') {
    throw 'Production signing requires an approved hosted release environment.'
}
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
. (Join-Path $PSScriptRoot 'Common.ps1')
$metadata = Get-PackageMetadata
if ($env:GITHUB_REF -cne "refs/tags/v$($metadata.Version)") { throw 'Signing requires the exact version tag.' }
& (Join-Path $PSScriptRoot 'Test-BranchPolicy.ps1')
if ([string]::IsNullOrWhiteSpace($env:SPARKLE_PRIVATE_KEY)) { throw 'Release signing key is missing.' }
$seed = [Convert]::FromBase64String($env:SPARKLE_PRIVATE_KEY)
if ($seed.Length -ne 32) { throw 'Invalid release signing key length.' }
[Array]::Clear($seed, 0, $seed.Length)
$repo = Split-Path $PSScriptRoot -Parent
$publicKey = Join-Path $repo 'TjClient.Wpf/Updates/UpdatePublicKey.txt'
$env:SPARKLE_PUBLIC_KEY = (Get-Content -LiteralPath $publicKey -Raw).Trim()
if ([Convert]::FromBase64String($env:SPARKLE_PUBLIC_KEY).Length -ne 32) { throw 'Invalid release public key.' }
if (Test-Path $OutputDirectory) { throw 'Signed output directory must be fresh.' }
$names = @("$($metadata.ArtifactStem)-setup.exe", "$($metadata.ArtifactStem).zip")
$expected = @($names + 'SHA256SUMS.txt' | Sort-Object)
$actual = @(Get-ChildItem -LiteralPath $InputDirectory | Select-Object -ExpandProperty Name | Sort-Object)
if (Compare-Object $expected $actual) { throw 'Unexpected unsigned build assets.' }
$lines = foreach ($name in $names) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $InputDirectory $name)).Hash.ToLowerInvariant(), $name
}
if (Compare-Object @($lines | Sort-Object) @(Get-Content (Join-Path $InputDirectory 'SHA256SUMS.txt') | Sort-Object)) { throw 'Build checksums mismatch.' }
New-Item -ItemType Directory $OutputDirectory | Out-Null
foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $InputDirectory $name) $OutputDirectory }
$setup = Join-Path $OutputDirectory $names[0]
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($setup).ProductVersion.Trim() -cne $metadata.Version) { throw 'Installer ProductVersion mismatch.' }
try {
    # AppCastGenerator 2.9.0 receives the seed only through its environment.
    & $GeneratorPath --single-file $setup --file-version $metadata.Version --os windows-x64 --base-url "$($metadata.RepositoryUrl)/releases/download/v$($metadata.Version)" --appcast-output-directory $OutputDirectory --product-name 'TJCloudClient' --use-ed25519-signature-attribute 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Appcast generator failed.' }
}
finally {
    Remove-Item Env:SPARKLE_PRIVATE_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:SPARKLE_PUBLIC_KEY -ErrorAction SilentlyContinue
}
# ReleaseTools independently verifies signatures before sealing final checksums.
& $DotNet $VerifierDll seal $OutputDirectory $metadata.Version $metadata.RepositoryUrl $publicKey
if ($LASTEXITCODE -ne 0) { throw 'Independent signed release verification failed.' }
