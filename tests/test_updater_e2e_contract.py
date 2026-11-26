"""Offline guards only. These tests do not claim hosted installer acceptance."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import json
import sys

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'scripts/Test-AutoUpdateE2E.ps1'


class UpdaterE2EContractTests(unittest.TestCase):
    def test_workstation_refused_before_any_path_or_install_access(self):
        self.assertTrue(SCRIPT.is_file(), 'hosted-only auto-update harness is missing')
        pwsh = shutil.which('pwsh')
        if not pwsh:
            self.skipTest('PowerShell unavailable: native guard must run in Windows CI')
        env = os.environ.copy()
        # Only REMOVE authority. Never spoof hosted-runner permissions on a workstation.
        for key in ('GITHUB_ACTIONS', 'RUNNER_ENVIRONMENT', 'RUNNER_OS', 'RUNNER_TEMP'):
            env.pop(key, None)
        result = subprocess.run([pwsh, '-NoProfile', '-File', str(SCRIPT), '-GuardOnly'],
                                env=env, capture_output=True, text=True, timeout=30)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('HOSTED_WINDOWS_REQUIRED', result.stdout + result.stderr)


    def test_preparation_cannot_turn_guard_probe_into_success(self):
        pwsh = shutil.which('pwsh')
        if not pwsh:
            self.skipTest('PowerShell unavailable: native guard must run in Windows CI')
        result = subprocess.run([pwsh, '-NoProfile', '-File', str(SCRIPT),
                                 '-PrepareOnly', '-GuardOnly'],
                                capture_output=True, text=True, timeout=30)
        self.assertNotEqual(0, result.returncode, 'contradictory modes must not report guard success')

    def test_artifact_paths_are_literal_in_real_powershell_commands(self):
        pwsh = shutil.which('pwsh')
        if not pwsh:
            self.skipTest('PowerShell unavailable: literal-path probe requires pwsh')
        # Parse, but never dot-source or execute the installer-bearing script.
        probe = r'''
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($env:E2E_SOURCE, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Harness parser errors' }
$root = Join-Path $env:E2E_PROBE_ROOT 'root [ab] ! % & spaces'
$case = Join-Path $root 'case [cd] ! % & spaces'
$destination = Join-Path $case 'installed client'
$newVersion = '1.0.1'
$file = 'TjClient.Tests.dll'
[IO.Directory]::CreateDirectory($destination) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $root "payload-$newVersion")) | Out-Null
[IO.File]::WriteAllText((Join-Path $destination $file), 'synthetic non-executable bytes')
[IO.File]::WriteAllText((Join-Path $root "payload-$newVersion/$file"), 'synthetic non-executable bytes')
[IO.File]::WriteAllText((Join-Path $case 'client-failure.json'), '{}')
$commands = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.CommandAst] -and (
        ($node.GetCommandName() -eq 'Get-FileHash' -and ($node.Extent.Text.Contains('(Join-Path $destination $file)') -or $node.Extent.Text.Contains('(Join-Path $root "payload-$newVersion/$file")'))) -or
        ($node.GetCommandName() -eq 'Test-Path' -and $node.Extent.Text.Contains("'client-failure.json'")))
}, $true))
if ($commands.Count -ne 4) { throw 'Expected both failure probes and both payload hashes' }
$hashes = @(); $failures = @()
foreach ($command in $commands) {
    # Only the selected filesystem commands are evaluated against synthetic scratch files.
    $value = @(& ([scriptblock]::Create($command.Extent.Text)))
    if ($command.GetCommandName() -eq 'Get-FileHash') {
        if ($value.Count -ne 1) { $failures += 'HARNESS02: hash did not resolve a literal file' }
        else { $hashes += $value[0].Hash }
    } elseif ($value.Count -ne 1 -or $value[0] -ne $true) { $failures += 'HARNESS02: failure record not found' }
}
if ($failures.Count) { throw ($failures -join ', ') }
if ($hashes.Count -ne 2 -or $hashes[0] -cne $hashes[1]) { throw 'Literal payload hashes differ' }
@{ parserErrors = $errors.Count; commands = $commands.Count; hashes = $hashes.Count; failureRecords = 2 } | ConvertTo-Json -Compress
'''
        with tempfile.TemporaryDirectory(prefix='updater-literal-') as temporary:
            env = os.environ.copy()
            env.update(E2E_SOURCE=str(SCRIPT), E2E_PROBE_ROOT=temporary)
            result = subprocess.run([pwsh, '-NoProfile', '-Command', probe],
                                    env=env, capture_output=True, text=True, timeout=30)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual({'parserErrors': 0, 'commands': 4, 'hashes': 2, 'failureRecords': 2},
                         json.loads(result.stdout))

    def test_process_launch_paths_and_streams_are_literal(self):
        pwsh = shutil.which('pwsh')
        if not pwsh or os.name != 'nt':
            self.skipTest('Windows PowerShell process-path regression')
        probe = r'''
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($env:E2E_SOURCE, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Harness parser errors' }
$commands = @($ast.FindAll({ param($n)
    $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -in @('Start-Process', 'Start-LiteralProcess')
}, $true))
if ($commands.Count -ne 3) { throw 'Expected installer, server and old-client launch commands' }
foreach ($helper in $ast.FindAll({ param($n)
    $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in @('Start-LiteralProcess', 'Complete-ProcessOutput')
}, $true)) { . ([scriptblock]::Create($helper.Extent.Text)) }
$case = $env:E2E_PROBE_CASE
$destination = Join-Path $case 'installed [cd] ! % & client'
$serverExe = Join-Path $destination 'TjCloudClient.exe'
$inputPath = Join-Path $case 'input [ef] ! % & spaces.json'
$Path = $serverExe
$installCall = @($ast.FindAll({ param($n)
    $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Invoke-Installer' -and $n.Extent.Text.Contains("`$packages['0.9.0']")
}, $true))
if ($installCall.Count -ne 1) { throw 'Expected exact old-installer invocation' }
$Arguments = & ([scriptblock]::Create($installCall[0].CommandElements[2].Extent.Text))
$prefix = @('-S', '-B', "`"$env:E2E_CHILD`"")
if ($commands[0].GetCommandName() -eq 'Start-LiteralProcess') {
    $prefix = @('-S', '-B', $env:E2E_CHILD)

}
$observations = @()
foreach ($command in $commands) {
    $text = $command.Extent.Text
    $kind = if ($text.Contains('$serverExe')) { 'server' } elseif ($text.Contains('$destination')) { 'old' } else { 'installer' }
    $env:E2E_CHILD_REPORT = Join-Path $case "$kind.json"
    # Keep every filesystem operand byte-identical to the source command. Only
    # prepend the interpreter/script argv needed by the harmless stand-in EXE.
    $parameters = @($command.CommandElements)
    $argument = $null
    for ($i = 0; $i -lt $parameters.Count; $i++) {
        if ($parameters[$i] -is [Management.Automation.Language.CommandParameterAst] -and $parameters[$i].ParameterName -eq 'ArgumentList') {
            $argument = $parameters[$i + 1].Extent.Text
        }
    }
    if ($null -ne $argument) { $text = $text.Replace($argument, ('(@($prefix) + @(' + $argument + '))')) }
    else { $text += ' -ArgumentList $prefix' }
    $p = & ([scriptblock]::Create($text))
    try {
        $identity = $p.Id
        $started = $p.StartTime.ToFileTimeUtc()
        if (-not $p.WaitForExit(15000)) { throw 'Harmless child timeout' }
        $p.WaitForExit()
        if (Get-Command Complete-ProcessOutput -ErrorAction SilentlyContinue) { Complete-ProcessOutput $p }
        if ($p.ExitCode -ne 7) { throw 'Harmless child exit code changed' }
        $report = Get-Content -LiteralPath $env:E2E_CHILD_REPORT -Raw | ConvertFrom-Json
        if ($report.pid -ne $identity -or $started -le 0 -or $report.inherited -cne 'synthetic inherited ! % & []') { throw 'Identity or inherited environment changed' }
        $expected = @(switch ($kind) {
            'server' { @('--updater-integration-server', $inputPath) }
            'old' { @() }
            'installer' { @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS', '/NOICONS', "/DIR=$destination") }
        })
        if ((ConvertTo-Json -InputObject @($report.argv) -Compress) -cne (ConvertTo-Json -InputObject @($expected) -Compress)) { throw 'Child argv changed' }
        if ($kind -eq 'old' -and $report.cwd -cne $destination) { throw 'WorkingDirectory changed' }
        if ($kind -ne 'installer') {
            foreach ($stream in @('stdout', 'stderr')) {
                $log = Join-Path $case "$kind.$stream.log"
                if ([IO.File]::ReadAllText($log).Trim() -cne "synthetic-$stream") { throw 'Wrong literal log path or lost output' }
            }
        }
        $observations += @{ kind = $kind; exit = $p.ExitCode; identity = $true; argv = $true; streams = ($kind -ne 'installer') }
    } finally { $p.Dispose() }
}
@{ parserErrors = $errors.Count; launches = $observations } | ConvertTo-Json -Depth 4 -Compress
'''
        with tempfile.TemporaryDirectory(prefix='updater-process-') as temporary:
            case = Path(temporary) / 'root [ab] ! % & spaces' / 'case [cd] ! % & spaces'
            destination = case / 'installed [cd] ! % & client'
            destination.mkdir(parents=True)
            python_home = Path(sys.executable).parent
            shutil.copyfile(sys.executable, destination / 'TjCloudClient.exe')
            for pattern in ('python*.dll', 'vcruntime*.dll'):
                for library in python_home.glob(pattern):
                    shutil.copyfile(library, destination / library.name)
            child = case / 'child [ef] ! % & spaces.py'
            child.write_text('import json, os, sys\n'
                             'with open(os.environ["E2E_CHILD_REPORT"], "w") as f:\n'
                             '    json.dump(dict(pid=os.getpid(), argv=sys.argv[1:], cwd=os.getcwd(), inherited=os.environ.get("E2E_INHERITED")), f)\n'
                             'print("synthetic-stdout")\n'
                             'print("synthetic-stderr", file=sys.stderr)\n'
                             'sys.exit(7)\n', encoding='utf-8')
            env = os.environ.copy()
            env.update(E2E_SOURCE=str(SCRIPT), E2E_PROBE_CASE=str(case), E2E_CHILD=str(child),
                       E2E_INHERITED='synthetic inherited ! % & []', PYTHONHOME=str(python_home),
                       PYTHONDONTWRITEBYTECODE='1')
            result = subprocess.run([pwsh, '-NoProfile', '-Command', probe], env=env,
                                    capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=60)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        report = json.loads(result.stdout.strip().splitlines()[-1])
        self.assertEqual(0, report['parserErrors'])
        self.assertEqual({'server', 'old', 'installer'}, {p['kind'] for p in report['launches']})
        self.assertEqual(3, len(report['launches']))
        self.assertTrue(all(p['exit'] == 7 and p['identity'] and p['argv'] for p in report['launches']))
        self.assertEqual(2, sum(p['streams'] for p in report['launches']))

    def test_real_runtime_not_installer_interception(self):
        source = (ROOT / 'TjClient.Tests/UpdaterIntegration.cs').read_text(encoding='utf-8')
        self.assertIn('launch: null', source)
        self.assertIn('new SignedUpdateBackend', source)
        self.assertIn('new UpdateCoordinator', source)
        self.assertIn('TestTls.CreateHandler(ca)', source)
        self.assertIn('FileMode.CreateNew', source)
        self.assertIn('PRESTARTUP_STATE_EQUAL', source)
        self.assertIn('NO_DUPLICATE_ACCEPTED_UPLOAD', source)
        self.assertIn('TAMPER_NO_PREPARE_OR_CLOSE', source)
        self.assertIn('TAMPER_FAILURE_OUTCOME', source)
        self.assertIn('TAMPER_SIGNED_PACKAGE_SERVED', source)
        self.assertIn('var lastOutcome = await coordinator!.CheckAsync()', source)
        self.assertNotIn('ObservedBackend', source)
        self.assertNotIn('AuthorizeHandoff =', source)
        self.assertIn('coordinator = CreateCoordinator(', source)
        production = '\n'.join(p.read_text(encoding='utf-8') for p in (ROOT / 'TjClient.Wpf').rglob('*.cs'))
        self.assertNotIn('TJCLIENT_UPDATER_E2E_CONFIG', production)
        self.assertNotIn('--updater-integration', production)
        program = (ROOT / 'TjClient.Tests/Program.cs').read_text(encoding='utf-8')
        self.assertLess(program.index('UpdaterIntegration.ConfigEnvironment'), program.index('var tests ='))
        self.assertIn('args.Length == 0 &&', program)

    def test_target_version_is_metadata_driven_and_handoff_not_cmd_specific(self):
        script = SCRIPT.read_text(encoding='utf-8')
        client = (ROOT / 'TjClient.Tests/UpdaterIntegration.cs').read_text(encoding='utf-8')
        self.assertIn('$newVersion = $metadata.Version', script)
        self.assertIn('TargetVersion = $newVersion', script)
        self.assertIn('string TargetVersion', client)
        self.assertIn('ValidateTargetVersion', client)
        self.assertIn('PackageUrl(config.TargetVersion)', client)
        self.assertIn('PackageUrl(input.TargetVersion)', client)
        self.assertNotIn('1.0.0', client)
        self.assertNotIn('1.0.0', script)
        self.assertNotIn('*.cmd', script)
        self.assertNotIn('tasklist', script)
        self.assertIn('installer-observed.json', script)
        self.assertIn('installer-observed.json', client)
        self.assertIn('$process.CommandLine', script)
        self.assertIn('[ab] ! % & spaces', script)

    def test_frozen_handoff_observes_exact_creation_identity_and_ready(self):
        script = SCRIPT.read_text(encoding='utf-8')
        client = (ROOT / 'TjClient.Tests/UpdaterIntegration.cs').read_text(encoding='utf-8')
        self.assertIn('/WAITSTART=', script)
        self.assertIn('/WAITPID=', script)
        self.assertIn('EventWaitHandle]::OpenExisting', script)
        self.assertIn('ToFileTimeUtc()', script)
        self.assertIn('readyObserved', client)
        self.assertIn('oldStartFileTime', client)
        self.assertIn('source-manifest.json', script)
        self.assertIn("(Join-Path $source 'packaging/TjCloudClient.iss')", script)
        self.assertTrue('$updateInstaller.ExitCode -ne 0' in script, 'real update installer exit must be checked')
        self.assertIn('getWindow()).LaunchAsync(launch, token)', client)
        self.assertIn('getWindow()).CloseAsync(commit, token)', client)

    def test_runner_matrix_and_real_install_gates_are_not_skipped(self):
        source = SCRIPT.read_text(encoding='utf-8')
        self.assertIn("@('running', 'paused', 'tampered')", source)
        self.assertIn('$installerPids.Count -ne 1', source)
        self.assertIn('$old.ExitCode -ne 0', source)
        self.assertIn('$client.ExitCode -ne 0', source)
        self.assertIn("productionExeExactE2E = $false", source)
        self.assertIn("'packaging/TjCloudClient.iss'", source)
        self.assertNotIn('Stop-Process', source)
        self.assertNotIn('-Force', source)


if __name__ == '__main__':
    unittest.main()
