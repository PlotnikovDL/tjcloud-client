[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SetupPath,
    [Parameter(Mandatory)][string]$VerifierDll,
    [Parameter(Mandatory)][string]$WorkRoot,
    [string]$GeneratorPath,
    [string]$DotNet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
. (Join-Path $PSScriptRoot 'Common.ps1')
$metadata = Get-PackageMetadata
if (Test-Path $WorkRoot) { throw 'Signing test output must be fresh.' }
& $DotNet $VerifierDll self-test $SetupPath (Join-Path $WorkRoot 'crypto') $metadata.Version
if ($LASTEXITCODE -ne 0) { throw 'Real cryptographic release tests failed.' }
if ($GeneratorPath) {
    & $DotNet $VerifierDll generator-test $GeneratorPath $SetupPath (Join-Path $WorkRoot 'generator') $metadata.Version
    if ($LASTEXITCODE -ne 0) { throw 'Hosted generator integration failed.' }
}
else { Write-Host 'Generator CLI integration was not executed. Supply GeneratorPath on a hosted runner.' }
