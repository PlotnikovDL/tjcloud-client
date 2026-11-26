[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SetupPath,
    [Parameter(Mandatory)][string]$PublishDirectory,
    [string]$RestartProbeOldSetupPath,
    [string]$RestartProbeSetupPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows' -or -not $env:RUNNER_TEMP) {
    throw 'Only an ephemeral GitHub-hosted Windows runner may execute installer acceptance. Never run on a user workstation.'
}
. (Join-Path $PSScriptRoot 'Common.ps1')
if (-not $RestartProbeOldSetupPath -or -not $RestartProbeSetupPath) { throw 'Hosted acceptance requires both synthetic restart probe installers.' }
$metadata = Get-PackageMetadata
$SetupPath = (Resolve-Path -LiteralPath $SetupPath).Path
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$destination = Join-Path $env:ProgramFiles 'TJCloudClient'
$profile = Join-Path $env:LOCALAPPDATA 'TJCloudClient'
$key = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$($metadata.ProductCode)"
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $profile) -or (Test-Path -LiteralPath $key)) { throw 'Runner is not clean. Refusing to touch existing installation or profile.' }
if (Get-Process -Name TjCloudClient -ErrorAction SilentlyContinue) { throw 'A client is already running. It will not be closed.' }
New-Item -ItemType Directory -Path $profile | Out-Null
[IO.File]::WriteAllBytes((Join-Path $profile 'preserve-profile.fixture'), [byte[]](1, 3, 5, 7, 11))
$profileBefore = @(Get-ChildItem -LiteralPath $profile -File -Recurse | ForEach-Object { "$($_.FullName):$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" })
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupBefore = if (Test-Path $runKey) { Get-ItemProperty $runKey | ConvertTo-Json -Compress } else { '' }
$legacyKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$($metadata.ProductCode)"
$legacyDirectory = Join-Path $env:LOCALAPPDATA 'Programs/TJCloudClient'
if ((Test-Path -LiteralPath $legacyKey) -or (Test-Path -LiteralPath $legacyDirectory)) { throw 'Unexpected existing user installation.' }
$legacySetup = Join-Path $env:RUNNER_TEMP 'published-1.0.1.exe'
Invoke-WebRequest -Uri "$($metadata.RepositoryUrl)/releases/download/v1.0.1/TJCloudClient-1.0.1-win-x64-setup.exe" -OutFile $legacySetup
if ((Get-FileHash -LiteralPath $legacySetup).Hash -ine '546cc636b7f0f537f5338f4f92d18eadaeb4fd8aa07dccdcf617861a1506d764') { throw 'Legacy release checksum mismatch.' }
$quiet = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART', '/NOICONS')
$legacy = Start-Process -FilePath $legacySetup -ArgumentList $quiet -Wait -PassThru
if ($legacy.ExitCode -ne 0 -or (Get-ItemProperty -LiteralPath $legacyKey).DisplayVersion -cne '1.0.1' -or
    (Get-ItemProperty -LiteralPath $legacyKey).InstallLocation.TrimEnd('\') -ine $legacyDirectory) { throw 'Published per-user installation failed.' }
$blockedUpgrade = Start-Process -FilePath $SetupPath -ArgumentList $quiet -Wait -PassThru
if ($blockedUpgrade.ExitCode -eq 0 -or (Test-Path -LiteralPath $key)) { throw 'A machine installation was allowed over a legacy user installation.' }
$removeLegacy = Start-Process -FilePath (Join-Path $legacyDirectory 'unins000.exe') -ArgumentList $quiet -Wait -PassThru
if ($removeLegacy.ExitCode -ne 0 -or (Test-Path -LiteralPath $legacyKey)) { throw 'Legacy uninstall failed.' }
& (Join-Path $PSScriptRoot 'Test-InstallerWizard.ps1') -SetupPath $SetupPath -ExpectedDirectory $destination
$install = Start-Process -FilePath $SetupPath -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS', '/NOICONS') -PassThru
try {
    if (-not $install.WaitForExit(120000)) { throw 'Installer timed out on the runner.' }
    if ($install.ExitCode -ne 0) { throw "Installer failed: $($install.ExitCode)" }
}
finally { $install.Dispose() }
if (Get-Process -Name TjCloudClient -ErrorAction SilentlyContinue) { throw 'Silent installation unexpectedly launched the client.' }
$registration = Get-ItemProperty -LiteralPath $key
if ($registration.InstallLocation.TrimEnd('\') -ine $destination) { throw 'Default destination must be the 64-bit Program Files directory.' }
if ($registration.DisplayName -ne $metadata.Product -or $registration.Publisher -ne $metadata.Publisher -or $registration.DisplayVersion -ne $metadata.Version) { throw 'Uninstall registration does not match package metadata.' }
foreach ($source in Get-ChildItem -LiteralPath $PublishDirectory -File -Recurse) {
    $relative = $source.FullName.Substring($PublishDirectory.Length).TrimStart('\', '/')
    $installed = Join-Path $destination $relative
    if (-not (Test-Path -LiteralPath $installed)) { throw "Installed payload is missing: $relative" }
    if ((Get-FileHash -LiteralPath $source.FullName).Hash -ne (Get-FileHash -LiteralPath $installed).Hash) { throw "Installed payload differs: $relative" }
}
& (Join-Path $PSScriptRoot 'Test-Smoke.ps1') -ExePath (Join-Path $destination 'TjCloudClient.exe') -ExpectedMode installed -ReportDirectory (Join-Path $env:RUNNER_TEMP 'gui-smoke')
$uninstall = Start-Process -FilePath (Join-Path $destination 'unins000.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -PassThru
try {
    if (-not $uninstall.WaitForExit(120000)) { throw 'Uninstaller timed out on the runner.' }
    if ($uninstall.ExitCode -ne 0) { throw "Uninstaller failed: $($uninstall.ExitCode)" }
}
finally { $uninstall.Dispose() }
if ((Test-Path -LiteralPath $key) -or (Test-Path -LiteralPath (Join-Path $destination 'TjCloudClient.exe'))) { throw 'Uninstall left the application or registration behind.' }
foreach ($source in Get-ChildItem -LiteralPath $PublishDirectory -File -Recurse) {
    $relative = $source.FullName.Substring($PublishDirectory.Length).TrimStart('\', '/')
    if (Test-Path -LiteralPath (Join-Path $destination $relative)) { throw "Uninstall left payload: $relative" }
}
$profileAfter = @(Get-ChildItem -LiteralPath $profile -File -Recurse | ForEach-Object { "$($_.FullName):$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" })
if (Compare-Object $profileBefore $profileAfter) { throw 'Install, smoke or uninstall changed the profile.' }
$startupAfter = if (Test-Path $runKey) { Get-ItemProperty $runKey | ConvertTo-Json -Compress } else { '' }
if ($startupBefore -ne $startupAfter) { throw 'Install, smoke or uninstall changed autostart registration.' }
Write-Host 'PASS runner-only install, registration, exact payload, native smoke, uninstall, profile preservation and unchanged autostart.'

$probeDestination = Join-Path $env:RUNNER_TEMP 'restart-probe-app'
$report = Join-Path $env:RUNNER_TEMP 'restart-probe.jsonl'
if ((Test-Path $probeDestination) -or (Test-Path $report)) { throw 'Restart probe paths must be fresh.' }
$oldReport = $env:TJCLIENT_RESTART_PROBE_REPORT
$env:TJCLIENT_RESTART_PROBE_REPORT = $report
function Invoke-ProbeSetup([string]$Path, [string[]]$ExtraArguments, [bool]$ExpectFailure = $false) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS', '/NOICONS', "/DIR=`"$probeDestination`"") + $ExtraArguments
    $process = Start-Process -FilePath $Path -ArgumentList $arguments -PassThru
    try {
        if (-not $process.WaitForExit(120000)) { throw 'Restart probe installer timed out.' }
        if (($process.ExitCode -eq 0) -eq $ExpectFailure) { throw "Unexpected restart probe installer exit: $($process.ExitCode)" }
    }
    finally { $process.Dispose() }
}
try {
    Invoke-ProbeSetup $RestartProbeOldSetupPath @()
    if (Test-Path $report) { throw 'Old manual/silent probe installation launched a process.' }
    if ((Get-ItemProperty $key).DisplayVersion -ne '0.0.1') { throw 'Old probe version was not installed.' }
    Invoke-ProbeSetup $RestartProbeOldSetupPath @('/RESTARTCLIENT=0')
    if (Test-Path $report) { throw 'Invalid restart request launched a process.' }
    Invoke-ProbeSetup $RestartProbeOldSetupPath @('/RESTARTCLIENT=1', '/RESTARTCLIENT=1')
    if (Test-Path $report) { throw 'Duplicated restart request launched a process.' }
    $blocked = Join-Path $env:RUNNER_TEMP 'blocked-probe-target'
    if (Test-Path $blocked) { throw 'Blocked install target must be fresh.' }
    [IO.File]::WriteAllText($blocked, 'Synthetic file, not an installation directory.')
    Invoke-ProbeSetup $RestartProbeSetupPath @('/RESTARTCLIENT=1', "/DIR=`"$blocked`"") $true
    if (Test-Path $report) { throw 'Failed setup launched a process.' }
    Invoke-ProbeSetup $RestartProbeSetupPath @('/RESTARTCLIENT=1')
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not (Test-Path $report) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path $report)) { throw 'Guarded restart did not run. A pending reboot also intentionally blocks restart.' }
    Start-Sleep -Milliseconds 500
    $reports = @(Get-Content $report)
    if ($reports.Count -ne 1) { throw 'Expected exactly one guarded restart.' }
    $started = $reports[0] | ConvertFrom-Json
    if ($started.executable -ne (Join-Path $probeDestination 'TjCloudClient.exe') -or $started.directory -ne $probeDestination) { throw 'Restart used the wrong executable or working directory.' }
    if ((Get-ItemProperty $key).DisplayVersion -ne $metadata.Version) { throw 'New probe version was not installed.' }
    $uninstall = Start-Process -FilePath (Join-Path $probeDestination 'unins000.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -PassThru
    try {
        if (-not $uninstall.WaitForExit(120000) -or $uninstall.ExitCode -ne 0) { throw 'Probe uninstall failed.' }
    }
    finally { $uninstall.Dispose() }
    if ((Test-Path $key) -or (Test-Path (Join-Path $probeDestination 'TjCloudClient.exe'))) { throw 'Probe uninstall left its registration or executable.' }
    $profileAfter = @(Get-ChildItem -LiteralPath $profile -File -Recurse | ForEach-Object { "$($_.FullName):$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" })
    if (Compare-Object $profileBefore $profileAfter) { throw 'Restart probe changed the synthetic profile.' }
    $startupAfter = if (Test-Path $runKey) { Get-ItemProperty $runKey | ConvertTo-Json -Compress } else { '' }
    if ($startupBefore -ne $startupAfter) { throw 'Restart probe changed autostart.' }
    Write-Host 'PASS synthetic installer upgrade and guarded single restart, profile bytes and autostart preserved. This is not full client auto-update acceptance.'
}
finally { $env:TJCLIENT_RESTART_PROBE_REPORT = $oldReport }
