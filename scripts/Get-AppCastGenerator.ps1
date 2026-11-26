[CmdletBinding()]
param([Parameter(Mandatory)][string]$ToolRoot, [string]$DotNet = 'dotnet')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (Test-Path $ToolRoot) { throw 'Use a fresh local tool directory.' }
& $DotNet tool install NetSparkleUpdater.Tools.AppCastGenerator --version 2.9.0 --tool-path $ToolRoot --framework net10.0
if ($LASTEXITCODE -ne 0) { throw 'Pinned appcast generator installation failed.' }
if (-not (Test-Path (Join-Path $ToolRoot 'netsparkle-generate-appcast.exe'))) { throw 'Generator executable is missing.' }
