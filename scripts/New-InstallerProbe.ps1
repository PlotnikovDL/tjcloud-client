[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ToolsDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$IsccPath,
    [string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Invalid probe version.' }
if (Test-Path $OutputDirectory) { throw 'Probe output must be fresh.' }
$repo = Split-Path $PSScriptRoot -Parent
$payload = Join-Path $OutputDirectory 'payload'
New-Item -ItemType Directory $payload | Out-Null
Copy-Item "$ToolsDirectory/*" $payload -Recurse
Copy-Item (Join-Path $payload 'ReleaseTools.exe') (Join-Path $payload 'TjCloudClient.exe')
Copy-Item (Join-Path $repo 'LICENSE') $payload
& $IsccPath "/DAppVersion=$Version" '/DProductName=TJ Cloud Client' '/DPublisher=TJCloud' '/DRepositoryUrl=https://github.com/PlotnikovDL/tjcloud-client' "/DPublishDirectory=$payload" "/DOutputDirectory=$OutputDirectory" "/DArtifactStem=TJCloudClient-$Version-win-x64" (Join-Path $repo 'packaging/TjCloudClient.iss')
if ($LASTEXITCODE -ne 0) { throw 'Synthetic installer probe compilation failed.' }
Write-Host 'Compiled synthetic installer probe. Not a distributable client. No installation performed.'
