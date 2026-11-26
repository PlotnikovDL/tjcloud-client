[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArtifactsRoot, [string]$DotNet = 'dotnet')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'tools/ReleaseTools.csproj'
& $DotNet restore $project --locked-mode "-p:ArtifactsPath=$ArtifactsRoot"
if ($LASTEXITCODE -ne 0) { throw 'ReleaseTools locked restore failed.' }
& $DotNet build $project -c Release --no-restore "-p:ArtifactsPath=$ArtifactsRoot"
if ($LASTEXITCODE -ne 0) { throw 'ReleaseTools build failed.' }
