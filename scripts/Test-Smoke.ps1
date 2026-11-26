[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExePath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
$app = Split-Path $ExePath -Parent
$runtime = Join-Path $app 'coreclr.dll'
if (-not (Test-Path -LiteralPath $runtime)) { throw 'Expected a self-contained application with coreclr.dll.' }
$reportDirectory = [IO.Path]::GetFullPath((Join-Path $app '../gui-smoke'))
$report = Join-Path $reportDirectory 'window-smoke.txt'
if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $ExePath
$start.Arguments = '--smoke-test'
$start.UseShellExecute = $false
$start.WorkingDirectory = $app
$start.EnvironmentVariables['PATH'] = (Join-Path $env:SystemRoot 'System32')
$start.EnvironmentVariables['DOTNET_ROOT'] = (Join-Path $app 'no-external-runtime')
$start.EnvironmentVariables['DOTNET_ROOT_X64'] = $start.EnvironmentVariables['DOTNET_ROOT']
$started = [DateTime]::UtcNow
$process = [Diagnostics.Process]::Start($start)
$loadedRuntime = $null
try {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not $process.HasExited -and $watch.Elapsed.TotalSeconds -lt 45) {
        if (-not $loadedRuntime) {
            $process.Refresh()
            $module = $process.Modules | Where-Object ModuleName -eq 'coreclr.dll' | Select-Object -First 1
            if ($module) { $loadedRuntime = $module.FileName }
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $process.HasExited) { $process.Kill(); throw 'The owned smoke process timed out.' }
    if ($process.ExitCode -ne 0) { throw "Smoke process failed: $($process.ExitCode)" }
    if ($loadedRuntime -ne $runtime) { throw "Smoke did not load the bundled coreclr.dll: $loadedRuntime" }
    if (-not (Test-Path -LiteralPath $report)) { throw 'Smoke report is missing.' }
    if ((Get-Item -LiteralPath $report).LastWriteTimeUtc -lt $started) { throw 'Smoke report is stale.' }
    if (-not (Get-Content -LiteralPath $report -Raw).StartsWith('PASS ')) { throw 'Smoke report did not pass.' }
    [ordered]@{
        ExeSha256 = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
        CoreClrSha256 = (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash
        LoadedRuntime = $loadedRuntime
        ReportSha256 = (Get-FileHash -LiteralPath $report -Algorithm SHA256).Hash
        ExitCode = $process.ExitCode
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportDirectory 'smoke-evidence.json') -Encoding UTF8
    Write-Host 'PASS native smoke loaded the bundled runtime and wrote a fresh report.'
}
finally { $process.Dispose() }
