[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SetupPath,
    [Parameter(Mandatory)][string]$PublishDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows' -or -not $env:RUNNER_TEMP) {
    throw 'Only an ephemeral GitHub-hosted Windows runner may execute installer acceptance. Never run on a user workstation.'
}
. (Join-Path $PSScriptRoot 'Common.ps1')
$metadata = Get-PackageMetadata
$SetupPath = (Resolve-Path -LiteralPath $SetupPath).Path
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$destination = Join-Path $env:RUNNER_TEMP 'probe'
$profile = Join-Path $env:LOCALAPPDATA 'TJCloudClient'
$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$($metadata.ProductCode)"
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $profile) -or (Test-Path -LiteralPath $key)) { throw 'Runner is not clean. Refusing to touch existing installation or profile.' }
if (Get-Process -Name TjCloudClient -ErrorAction SilentlyContinue) { throw 'A client is already running. It will not be closed.' }
New-Item -ItemType Directory -Path $profile | Out-Null
[IO.File]::WriteAllBytes((Join-Path $profile 'preserve-profile.fixture'), [byte[]](1, 3, 5, 7, 11))
$profileBefore = @(Get-ChildItem -LiteralPath $profile -File -Recurse | ForEach-Object { "$($_.FullName):$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" })
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupBefore = if (Test-Path $runKey) { Get-ItemProperty $runKey | ConvertTo-Json -Compress } else { '' }
$install = Start-Process -FilePath $SetupPath -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS', '/NOICONS', "/DIR=`"$destination`"") -PassThru
try {
    if (-not $install.WaitForExit(120000)) { throw 'Installer timed out on the runner.' }
    if ($install.ExitCode -ne 0) { throw "Installer failed: $($install.ExitCode)" }
}
finally { $install.Dispose() }
if (Get-Process -Name TjCloudClient -ErrorAction SilentlyContinue) { throw 'Silent installation unexpectedly launched the client.' }
$registration = Get-ItemProperty -LiteralPath $key
if ($registration.DisplayName -ne $metadata.Product -or $registration.Publisher -ne $metadata.Publisher -or $registration.DisplayVersion -ne $metadata.Version) { throw 'Uninstall registration does not match package metadata.' }
foreach ($source in Get-ChildItem -LiteralPath $PublishDirectory -File -Recurse) {
    $relative = $source.FullName.Substring($PublishDirectory.Length).TrimStart('\', '/')
    $installed = Join-Path $destination $relative
    if (-not (Test-Path -LiteralPath $installed)) { throw "Installed payload is missing: $relative" }
    if ((Get-FileHash -LiteralPath $source.FullName).Hash -ne (Get-FileHash -LiteralPath $installed).Hash) { throw "Installed payload differs: $relative" }
}
& (Join-Path $PSScriptRoot 'Test-Smoke.ps1') -ExePath (Join-Path $destination 'TjCloudClient.exe')
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
