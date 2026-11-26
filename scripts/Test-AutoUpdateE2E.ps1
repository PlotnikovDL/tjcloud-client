[CmdletBinding()]
param(
    [string]$ArtifactsRoot,
    [string]$IsccPath,
    [switch]$PrepareOnly,
    [switch]$GuardOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PrepareOnly -and $GuardOnly) { throw 'PrepareOnly and GuardOnly are mutually exclusive.' }
# Preparation compiles only. Every installer-bearing path requires real runner authority.
if (-not $PrepareOnly) {
    if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows' -or -not $env:RUNNER_TEMP) {
        throw 'HOSTED_WINDOWS_REQUIRED: never execute installers or spoof these guards on a workstation.'
    }
}
if ($GuardOnly) { return }
if (-not $ArtifactsRoot -or -not $IsccPath) { throw 'ArtifactsRoot and IsccPath are required.' }
$root = [IO.Path]::GetFullPath($ArtifactsRoot)
if (Test-Path -LiteralPath $root) { throw 'ArtifactsRoot must be a fresh directory.' }
if (-not $PrepareOnly -and -not $root.StartsWith([IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be strictly below RUNNER_TEMP.' }
for ($ancestor = [IO.DirectoryInfo]$root; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse-point output rejected.' }
}
$compiler = (Resolve-Path -LiteralPath $IsccPath).Path
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Common.ps1')
$metadata = Get-PackageMetadata
$newVersion = $metadata.Version
if ([version]$newVersion -le [version]'0.9.0') { throw 'Central numeric version must be newer than the internal old fixture.' }
New-Item -ItemType Directory -Path $root | Out-Null
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_HOME = Join-Path $root 'dotnet-home'
# A short isolated package cache avoids silent omissions at the Windows extraction path limit.
$env:NUGET_PACKAGES = Join-Path $root 'n'
# The checked-in locks are portable. Create RID-specific locks only in an exact source snapshot.
$source = Join-Path $root 'source'
New-Item -ItemType Directory -Path $source | Out-Null
foreach ($name in @('Directory.Build.props', 'global.json', 'LICENSE', 'TjClient.Core', 'TjClient.Updates', 'TjClient.Tests', 'TjClient.Wpf', 'packaging')) {
    Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $source -Recurse
}
$sourceHashes = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File | Sort-Object FullName) {
    $relative = $file.FullName.Substring($source.Length).TrimStart('\', '/').Replace('\', '/')
    $sourceHashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
@{ schema = 1; targetVersion = $newVersion; sourceSha256 = $sourceHashes } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'source-manifest.json')
$portableLocks = @{}
foreach ($project in @('TjClient.Core', 'TjClient.Updates', 'TjClient.Tests')) {
    $portableLocks[$project] = Get-Content -LiteralPath (Join-Path $source "$project/packages.lock.json") -Raw | ConvertFrom-Json -AsHashtable
}
& dotnet restore (Join-Path $source 'TjClient.Tests/TjClient.Tests.csproj') --locked-mode "-p:ArtifactsPath=$root/portable-build" *> (Join-Path $root 'locked-restore.log')
if ($LASTEXITCODE -ne 0) { throw 'Portable locked restore failed.' }
$packages = @{}
foreach ($version in @('0.9.0', $newVersion)) {
    $payload = Join-Path $root "payload-$version"
    & dotnet publish (Join-Path $source 'TjClient.Tests/TjClient.Tests.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$version" "-p:ArtifactsPath=$root/build-$version" -p:RestoreLockedMode=false -o $payload *> (Join-Path $root "publish-$version.log")
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $version. See isolated publish log." }
    foreach ($project in $portableLocks.Keys) {
        $ridLock = Get-Content -LiteralPath (Join-Path $source "$project/packages.lock.json") -Raw | ConvertFrom-Json -AsHashtable
        foreach ($target in $portableLocks[$project].dependencies.Keys) {
            $before = $portableLocks[$project].dependencies[$target] | ConvertTo-Json -Depth 50 -Compress
            $after = $ridLock.dependencies[$target] | ConvertTo-Json -Depth 50 -Compress
            if ($before -cne $after) { throw 'RID restore changed a locked portable dependency.' }
        }
    }
    # Preserve TjClient.Tests identity and embedded DLL name for friend access.
    Copy-Item -LiteralPath (Join-Path $payload 'TjClient.Tests.exe') -Destination (Join-Path $payload 'TjCloudClient.exe')
    Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination $payload
    & $compiler "/DAppVersion=$version" "/DProductName=$($metadata.Product)" "/DPublisher=$($metadata.Publisher)" "/DRepositoryUrl=$($metadata.RepositoryUrl)" "/DOutputDirectory=$root" "/DPublishDirectory=$payload" "/DArtifactStem=UpdaterIntegration-$version" (Join-Path $source 'packaging/TjCloudClient.iss') *> (Join-Path $root "inno-$version.log")
    if ($LASTEXITCODE -ne 0) { throw "Inno compilation failed: $version." }
    $packages[$version] = Join-Path $root "UpdaterIntegration-$version-setup.exe"
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($packages[$version]).ProductVersion.Trim() -cne $version) { throw 'Compiled installer metadata mismatch.' }
}
@{ status = 'PREPARED_NOT_EXECUTED'; bootstrap = 'TjClient.Tests with linked production WPF/Core/Updates'; installerExecuted = $false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'preparation.json')
if ($PrepareOnly) { Write-Host 'PREPARED_NOT_EXECUTED: no installer was run.'; return }

$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$($metadata.ProductCode)"
$defaultInstall = Join-Path $env:LOCALAPPDATA 'Programs/TJCloudClient'
if ((Test-Path -LiteralPath $key) -or (Test-Path -LiteralPath $defaultInstall) -or (Get-Process -Name TjCloudClient -ErrorAction SilentlyContinue)) { throw 'Unexpected existing client or registration. Nothing will be stopped or overwritten.' }
function Wait-Record([string]$Path, [int]$Seconds = 120) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $until) {
        if (Test-Path -LiteralPath $Path) { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
        Start-Sleep -Milliseconds 100
    }
    throw "Timeout waiting for $([IO.Path]::GetFileName($Path))."
}
function Start-LiteralProcess([string]$FilePath, [string[]]$ArgumentList = @(), [string]$WorkingDirectory,
    [string]$RedirectStandardOutput, [string]$RedirectStandardError) {
    $info = [Diagnostics.ProcessStartInfo]::new($FilePath)
    $info.UseShellExecute = $false
    foreach ($argument in $ArgumentList) { $info.ArgumentList.Add($argument) }
    if ($WorkingDirectory) { $info.WorkingDirectory = $WorkingDirectory }
    $files = @(); $copies = @(); $started = $false
    $p = [Diagnostics.Process]::new()
    try {
        if ($RedirectStandardOutput) {
            $files += [IO.File]::Open($RedirectStandardOutput, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            $info.RedirectStandardOutput = $true
        }
        if ($RedirectStandardError) {
            $files += [IO.File]::Open($RedirectStandardError, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            $info.RedirectStandardError = $true
        }
        $p.StartInfo = $info
        $started = $p.Start()
        if (-not $started) { throw 'Owned process did not start.' }
        $index = 0
        if ($RedirectStandardOutput) { $copies += $p.StandardOutput.BaseStream.CopyToAsync($files[$index++]) }
        if ($RedirectStandardError) { $copies += $p.StandardError.BaseStream.CopyToAsync($files[$index++]) }
        $p | Add-Member -NotePropertyName OutputCopies -NotePropertyValue $copies
        $p | Add-Member -NotePropertyName OutputFiles -NotePropertyValue $files
        return $p
    } finally {
        if (-not $started) { foreach ($file in $files) { $file.Dispose() }; $p.Dispose() }
    }
}
function Open-ObservedProcess([int]$ProcessId) {
    $observed = Get-Process -Id $ProcessId -ErrorAction Stop
    # Retain the handle while the process is alive so ExitCode survives its exit.
    [void]$observed.Handle
    return $observed
}
function Complete-ProcessOutput([Diagnostics.Process]$Process) {
    if ($null -eq $Process.PSObject.Properties['OutputCopies']) { return }
    try {
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]$Process.OutputCopies, 20000)) { throw 'Owned process output did not complete.' }
    } finally { foreach ($file in $Process.OutputFiles) { $file.Dispose() } }
}
function Invoke-Installer([string]$Path, [string[]]$Arguments) {
    $p = Start-LiteralProcess -FilePath $Path -ArgumentList $Arguments
    try { if (-not $p.WaitForExit(120000) -or $p.ExitCode -ne 0) { throw 'Owned installer failed or timed out.' } }
    finally { $p.Dispose() }
}
$results = @()
foreach ($scenario in @('running', 'paused', 'tampered')) {
    if (Test-Path -LiteralPath $key) { throw 'Unexpected installer registration before scenario.' }
    $case = Join-Path $root "$scenario [ab] ! % & spaces"
    New-Item -ItemType Directory -Path $case | Out-Null
    [IO.File]::WriteAllText((Join-Path $case '.tjclient-updater-e2e-owned'), [guid]::NewGuid().ToString('N'))
    $destination = Join-Path $case 'installed client'
    $inputPath = Join-Path $case 'server-input.json'
    @{ Root = $case; Setup = $packages[$newVersion]; InstallDirectory = $destination; TargetVersion = $newVersion; Running = ($scenario -ne 'paused'); Tamper = ($scenario -eq 'tampered') } | ConvertTo-Json | Set-Content -LiteralPath $inputPath
    $serverExe = Join-Path $root "payload-$newVersion/TjClient.Tests.exe"
    $priorTemp = $env:TEMP
    $priorTmp = $env:TMP
    $handoffTemp = Join-Path $case 'handoff-temp'
    New-Item -ItemType Directory -Path $handoffTemp | Out-Null
    $env:TEMP = $handoffTemp
    $env:TMP = $handoffTemp
    $server = Start-LiteralProcess -FilePath $serverExe -ArgumentList @('--updater-integration-server', $inputPath) -RedirectStandardOutput (Join-Path $case 'server.stdout.log') -RedirectStandardError (Join-Path $case 'server.stderr.log')
    $old = $null
    $new = $null
    $updateInstaller = $null
    $priorConfig = $env:TJCLIENT_UPDATER_E2E_CONFIG
    $installerPids = [Collections.Generic.HashSet[int]]::new()
    try {
        $ready = Wait-Record (Join-Path $case 'server-ready.json') 30
        if ($ready.pid -ne $server.Id -or $ready.version -cne $newVersion) { throw 'Wrong server identity or target version.' }
        Invoke-Installer $packages['0.9.0'] @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS', '/NOICONS', "/DIR=$destination")
        if ((Get-ItemProperty -LiteralPath $key).DisplayVersion -cne '0.9.0') { throw 'Old install registration mismatch.' }
        $oldAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $destination 'TjClient.Tests.dll')).Version.ToString()
        if ($oldAssemblyVersion -cne '0.9.0.0') { throw 'Old assembly version mismatch.' }
        $oldPayloadHash = (Get-FileHash -LiteralPath (Join-Path $destination 'TjClient.Tests.dll')).Hash
        $newAssemblyVersion = $null
        $env:TJCLIENT_UPDATER_E2E_CONFIG = Join-Path $case 'client.dpapi'
        $old = Start-LiteralProcess -FilePath (Join-Path $destination 'TjCloudClient.exe') -WorkingDirectory $destination -RedirectStandardOutput (Join-Path $case 'old.stdout.log') -RedirectStandardError (Join-Path $case 'old.stderr.log')
        $oldStartFileTime = $old.StartTime.ToFileTimeUtc().ToString([Globalization.CultureInfo]::InvariantCulture)
        $deadline = [DateTime]::UtcNow.AddMinutes(4)
        $terminalReport = Join-Path $case $(if ($scenario -eq 'tampered') { 'tampered-pass.json' } else { 'new-pass.json' })
        while ([DateTime]::UtcNow -lt $deadline -and -not (Test-Path -LiteralPath $terminalReport)) {
            if ((Test-Path -LiteralPath (Join-Path $case 'client-failure.json')) -or $server.HasExited) { throw 'Child failed. See safe failure stage report.' }
            # Observe only executable paths in this owned update directory, never kill by process name.
            foreach ($process in Get-CimInstance Win32_Process -Filter "Name LIKE 'update-%.exe'") {
                if ($process.ExecutablePath -and $process.ExecutablePath.StartsWith([IO.Path]::GetFullPath((Join-Path $case 'state/updates')) + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    [void]$installerPids.Add([int]$process.ProcessId)
                    if ($null -eq $updateInstaller) {
                        $updateInstaller = Open-ObservedProcess $process.ProcessId
                    }
                    $observation = Join-Path $case 'installer-observed.json'
                    if (-not (Test-Path -LiteralPath $observation)) {
                        $commandLine = $process.CommandLine
                        foreach ($argument in @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS', '/RESTARTCLIENT=1')) {
                            if ($commandLine -notmatch ('(?i)(?:^|\s)' + [regex]::Escape($argument) + '(?=\s|$)')) { throw 'Installer arguments differ from the automatic update contract.' }
                        }
                        $pidArguments = [regex]::Matches($commandLine, '(?i)(?:^|\s)/WAITPID=([0-9]+)(?=\s|$)')
                        $startArguments = [regex]::Matches($commandLine, '(?i)(?:^|\s)/WAITSTART=([0-9]+)(?=\s|$)')
                        if ($pidArguments.Count -ne 1 -or $pidArguments[0].Groups[1].Value -cne [string]$old.Id -or $startArguments.Count -ne 1 -or $startArguments[0].Groups[1].Value -cne $oldStartFileTime) { throw 'Installer was not given the exact old PID and creation FILETIME.' }
                        if ((Get-FileHash -LiteralPath $process.ExecutablePath).Hash -ine $ready.installerHash) { throw 'Running installer bytes differ from the signed package.' }
                        $closeEntered = Wait-Record (Join-Path $case 'old-close-enter.json') 35
                        if ($closeEntered.pid -ne $old.Id) { throw 'Wrong old process at handoff.' }
                        $eventPrefix = "Local\TJCloudClient.Update.$($old.Id).$oldStartFileTime"
                        $readyEvent = [Threading.EventWaitHandle]::OpenExisting("$eventPrefix.Ready")
                        $abortEvent = $null
                        $stoppedEvent = $null
                        try {
                            $abortEvent = [Threading.EventWaitHandle]::OpenExisting("$eventPrefix.Abort")
                            $stoppedEvent = [Threading.EventWaitHandle]::OpenExisting("$eventPrefix.Stopped")
                            $holdUntil = [DateTime]::UtcNow.AddSeconds(2)
                            while ([DateTime]::UtcNow -lt $holdUntil) {
                                if (-not $readyEvent.WaitOne(0) -or $abortEvent.WaitOne(0) -or $stoppedEvent.WaitOne(0)) { throw 'Installer is not in the validated ready/wait state.' }
                                if ($old.HasExited -or (Get-FileHash -LiteralPath (Join-Path $destination 'TjClient.Tests.dll')).Hash -cne $oldPayloadHash) { throw 'Installer changed payload before the old PID exited.' }
                                Start-Sleep -Milliseconds 100
                            }
                        }
                        finally {
                            $readyEvent.Dispose()
                            if ($null -ne $abortEvent) { $abortEvent.Dispose() }
                            if ($null -ne $stoppedEvent) { $stoppedEvent.Dispose() }
                        }
                        $observed = @{ installerPid = [int]$process.ProcessId; installerCommandLine = $commandLine; installerSha256 = $ready.installerHash; oldPid = $old.Id; oldStartFileTime = $oldStartFileTime; readyObserved = $true; oldStillAlive = $true; oldPayloadUnchanged = $true; requiredArgumentsPresent = $true; oldPidArgumentPresent = $true }
                        $observed | ConvertTo-Json | Set-Content -LiteralPath "$observation.tmp"
                        Move-Item -LiteralPath "$observation.tmp" -Destination $observation
                    }
                }
            }
            Start-Sleep -Milliseconds 100
        }
        if (-not (Test-Path -LiteralPath $terminalReport)) { throw 'Cross-process acceptance timeout. Not a PASS.' }
        $report = Get-Content -LiteralPath $terminalReport -Raw | ConvertFrom-Json
        if ($scenario -eq 'tampered') {
            if ($report.status -cne 'TAMPER_REJECTED' -or $installerPids.Count -ne 0 -or $old.HasExited -or (Get-ItemProperty $key).DisplayVersion -cne '0.9.0') { throw 'Tampered package did not fail closed.' }
        } else {
            $observed = Wait-Record (Join-Path $case 'installer-observed.json') 1
            if ($observed.oldPid -ne $old.Id -or $observed.oldStartFileTime -cne $oldStartFileTime -or -not $observed.readyObserved -or -not $observed.oldStillAlive -or -not $observed.oldPayloadUnchanged) { throw 'Installer PID-wait evidence is missing.' }
            $oldStart = Wait-Record (Join-Path $case 'old-start.json') 1
            $drained = Wait-Record (Join-Path $case 'old-drained.json') 1
            $closed = Wait-Record (Join-Path $case 'old-close.json') 1
            if ($oldStart.pid -ne $old.Id -or $oldStart.version -cne '0.9.0' -or $drained.pid -ne $old.Id -or $drained.activeOperations -ne 0 -or $drained.busy -or $closed.closeCount -ne 1) { throw 'Old process identity, safe drain or single handoff evidence mismatch.' }
            if (-not $old.WaitForExit(20000) -or $old.ExitCode -ne 0) { throw 'Old process did not exit cleanly.' }
            if ($null -eq $updateInstaller -or -not $updateInstaller.WaitForExit(60000) -or $updateInstaller.ExitCode -ne 0) { throw 'Real update installer failed or timed out.' }
            if ($report.status -cne 'PASS' -or $report.pid -eq $old.Id -or $report.version -cne $newVersion -or $installerPids.Count -ne 1) { throw 'Expected a distinct new process and exactly one observed installer launch.' }
            $new = Open-ObservedProcess $report.pid
            if ($new.Path -cne (Join-Path $destination 'TjCloudClient.exe')) { throw 'Wrong restart path.' }
            if ((Get-ItemProperty $key).DisplayVersion -cne $newVersion) { throw 'Upgrade registration mismatch.' }
            $newAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $destination 'TjClient.Tests.dll')).Version.ToString()
            if ($newAssemblyVersion -cne "$newVersion.0") { throw 'New assembly version mismatch.' }
            foreach ($file in @('TjClient.Tests.dll', 'TjClient.Core.dll', 'TjClient.Updates.dll')) {
                if ((Get-FileHash -LiteralPath (Join-Path $destination $file)).Hash -cne (Get-FileHash -LiteralPath (Join-Path $root "payload-$newVersion/$file")).Hash) { throw 'Installed assembly bytes differ from new published payload.' }
            }
            $runtime = @($new.Modules | Where-Object ModuleName -eq 'coreclr.dll')
            if ($runtime.Count -ne 1 -or -not $runtime[0].FileName.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Restart did not load self-contained runtime.' }
        }
        [IO.File]::WriteAllText((Join-Path $case 'client-stop'), 'stop')
        $client = if ($null -ne $new) { $new } else { $old }
        if (-not $client.WaitForExit(20000) -or $client.ExitCode -ne 0) { throw 'Client completion failed.' }
        if (Test-Path -LiteralPath (Join-Path $case 'client-failure.json')) { throw 'A second or failed client invocation was observed.' }
        $results += @{ scenario = $scenario; status = $report.status; oldPid = $old.Id; newPid = $report.pid; oldAssemblyVersion = $oldAssemblyVersion; newAssemblyVersion = $newAssemblyVersion; observedInstallerLaunches = $installerPids.Count }
    }
    finally {
        $env:TEMP = $priorTemp
        $env:TMP = $priorTmp
        $env:TJCLIENT_UPDATER_E2E_CONFIG = $priorConfig
        [IO.File]::WriteAllText((Join-Path $case 'client-stop'), 'stop')
        [IO.File]::WriteAllText((Join-Path $case 'server-stop'), 'stop')
        # No force-kill, no global process cleanup. An unresponsive owned process fails this job.
        foreach ($child in @($old, $new, $server, $updateInstaller)) {
            if ($null -ne $child) {
                if (-not $child.WaitForExit(20000)) { throw 'Owned child did not stop. Refusing destructive cleanup.' }
                Complete-ProcessOutput $child
                if ($null -eq $child.ExitCode) { throw "Missing exit code for child PID $($child.Id)." }
                if ($child.ExitCode -ne 0) { throw "Child PID $($child.Id) exited with code $($child.ExitCode)." }
                $child.Dispose()
            }
        }
        $uninstaller = Join-Path $destination 'unins000.exe'
        if (Test-Path -LiteralPath $uninstaller) {
            if (-not (Test-Path $key) -or [IO.Path]::GetFullPath((Get-ItemProperty $key).InstallLocation).TrimEnd('\') -cne $destination) { throw 'Uninstall ownership mismatch.' }
            Invoke-Installer $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
            if (Test-Path -LiteralPath $key) { throw 'Owned uninstall left registration.' }
        }
    }
}
@{ status = 'PASS'; bootstrap = 'linked production runtime, test-only entrypoint'; scenarios = $results; productionExeExactE2E = $false; failedInstallRecoveryCovered = $false } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'hosted-result.json')
Write-Host 'PASS hosted signed real-handoff/Inno/process/state acceptance. Test-only bootstrap, not exact production EXE.'
