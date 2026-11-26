[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$VerifierDll,
    [string]$DotNet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
. (Join-Path $PSScriptRoot 'Common.ps1')
$metadata = Get-PackageMetadata
$publicKey = Join-Path (Split-Path $PSScriptRoot -Parent) 'TjClient.Wpf/Updates/UpdatePublicKey.txt'
& $DotNet $VerifierDll verify $Directory $metadata.Version $metadata.RepositoryUrl $publicKey
if ($LASTEXITCODE -ne 0) { throw 'Release assets failed independent verification.' }
