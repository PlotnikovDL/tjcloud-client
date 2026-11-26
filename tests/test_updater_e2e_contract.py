"""Offline guards only. These tests do not claim hosted installer acceptance."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import json

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
