import hashlib
import json
import os
import re
import shutil
import sys
import subprocess
import tempfile
import unittest
from pathlib import Path
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[1]


class BuildContracts(unittest.TestCase):
    def test_repository_build_has_pinned_sdk_and_portable_metadata(self):
        props = ElementTree.parse(ROOT / 'Directory.Build.props').getroot()
        values = {node.tag: node.text for group in props for node in group}
        self.assertEqual('$(MSBuildThisFileDirectory)artifacts', values['ArtifactsPath'])
        self.assertEqual('true', values['TreatWarningsAsErrors'])
        self.assertEqual('true', values['RestorePackagesWithLockFile'])
        self.assertRegex(values['Version'], r'^\d+\.\d+\.\d+$')
        self.assertEqual('TJCloud', values['Company'])
        self.assertEqual('TJ Cloud Client', values['Product'])
        self.assertEqual('https://github.com/PlotnikovDL/tjcloud-client', values['RepositoryUrl'])
        self.assertEqual({'version': '10.0.302', 'rollForward': 'disable', 'allowPrerelease': False},
                         json.loads((ROOT / 'global.json').read_text())['sdk'])
    def test_build_entrypoint_requires_locked_restore_and_real_native_gates(self):
        build = ROOT / 'scripts' / 'Build.ps1'
        smoke = ROOT / 'scripts' / 'Test-Smoke.ps1'
        self.assertTrue(build.is_file(), 'Build.ps1 is required')
        self.assertTrue(smoke.is_file(), 'Test-Smoke.ps1 is required')
        script = build.read_text(encoding='utf-8')
        for required in ['--locked-mode', '--no-restore', 'TjClient.slnx',
                         'TjClient.Tests', '^RESULT total=',
                         '--self-contained', 'win-x64', 'Test-Smoke.ps1']:
            self.assertIn(required, script)
        self.assertIn('coreclr.dll', smoke.read_text(encoding='utf-8'))
    def test_installer_is_machine_wide_russian_and_preserves_user_state(self):
        installer = ROOT / 'packaging' / 'TjCloudClient.iss'
        self.assertTrue(installer.is_file(), 'Inno installer source is required')
        text = installer.read_text(encoding='utf-8')
        for line in ['PrivilegesRequired=admin',
                     'DefaultDirName={autopf}\\TJCloudClient',
                     'Name: "ru"; MessagesFile: "compiler:Languages\\Russian.isl"',
                     'LicenseFile=LICENSE.ru.txt',
                     'AllowNoIcons=yes', 'CloseApplications=no', 'RestartApplications=no',
                     'AppName={#ProductName}', 'AppVersion={#AppVersion}',
                     'AppPublisher={#Publisher}', 'ArchitecturesAllowed=x64compatible']:
            self.assertIn(line, text)
        for section in ['[UninstallRun]', '[UninstallDelete]', '[Registry]', '[InstallDelete]']:
            self.assertNotIn(section, text)
        self.assertIn('[Run]', text)
        self.assertIn('Filename: "{app}\\TjCloudClient.exe"; WorkingDir: "{app}"; Flags: nowait runasoriginaluser; Check: ShouldRestartClient', text)
        self.assertIn("ParamStr(I) = '/RESTARTCLIENT=1'", text)
        self.assertIn('function ShouldRestartClient: Boolean;', text)
        self.assertIn('function NeedRestart: Boolean;', text)
        self.assertIn('PendingFileRenameOperations', text)
        self.assertNotIn('skipifsilent', text)
        self.assertNotIn('postinstall', text)
        self.assertNotIn('Parameters:', text)
        self.assertRegex(text, r'AppId=\{\{[0-9A-Fa-f-]{36}\}')

    def test_package_rejects_incomplete_publish_before_creating_output(self):
        script = ROOT / 'scripts' / 'Package.ps1'
        self.assertTrue(script.is_file(), 'Package.ps1 is required')
        with tempfile.TemporaryDirectory(prefix='.packaging-tests-', dir=ROOT) as folder:
            output = Path(folder) / 'output'
            result = run_ps_file(script, '-PublishDirectory', folder, '-OutputDirectory', str(output), '-ZipOnly')
            self.assertNotEqual(0, result.returncode)
            self.assertIn('Incomplete publish', result.stdout + result.stderr)
            self.assertFalse(output.exists())
    def test_winget_generator_hashes_file_and_emits_three_schema_files(self):
        script = ROOT / 'scripts' / 'New-WinGetManifest.ps1'
        self.assertTrue(script.is_file(), 'WinGet generator is required')
        version = ElementTree.parse(ROOT / 'Directory.Build.props').find('./PropertyGroup/Version').text
        with tempfile.TemporaryDirectory(prefix='.packaging-tests-', dir=ROOT) as folder:
            fixture = Path(folder) / f'TJCloudClient-{version}-win-x64-setup.exe'
            # A genuine PE executable used only as a hash/schema fixture, not an application installer.
            shutil.copyfile(sys.executable, fixture)
            output = Path(folder) / 'manifests'
            url = f'https://github.com/PlotnikovDL/tjcloud-client/releases/download/v{version}/' + fixture.name
            result = run_ps_file(script, '-InstallerPath', str(fixture), '-Version', version,
                                 '-InstallerUrl', url, '-OutputDirectory', str(output), '-SchemaOnly')
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            files = sorted(output.rglob('*.yaml'))
            self.assertEqual(3, len(files))
            text = '\n'.join(file.read_text(encoding='utf-8-sig') for file in files)
            self.assertIn('SCHEMA ONLY', text)
            self.assertIn(hashlib.sha256(fixture.read_bytes()).hexdigest().upper(), text)
            self.assertEqual(3, text.count('ManifestVersion: 1.28.0'))
            self.assertIn(f"PrivacyUrl: 'https://github.com/PlotnikovDL/tjcloud-client/blob/v{version}/PRIVACY.md'", text)
            self.assertTrue((ROOT / 'PRIVACY.md').is_file())
            for expected in ['ManifestType: version',
                             'ManifestType: defaultLocale', 'ManifestType: installer',
                             'Scope: machine', 'ElevationRequirement: elevationRequired', 'InstallerLocale: ru-RU',
                             'InstallerType: inno', 'AppsAndFeaturesEntries:',
                             '12ca27e9-70d7-48f0-b0ac-350146b5578a}_is1']:
                self.assertIn(expected, text)

    def test_winget_generator_rejects_invalid_version_url_or_binary(self):
        script = ROOT / 'scripts' / 'New-WinGetManifest.ps1'
        self.assertTrue(script.is_file(), 'WinGet generator is required')
        version = ElementTree.parse(ROOT / 'Directory.Build.props').find('./PropertyGroup/Version').text
        with tempfile.TemporaryDirectory(prefix='.packaging-tests-', dir=ROOT) as folder:
            fixture = Path(folder) / f'TJCloudClient-{version}-win-x64-setup.exe'
            fixture.write_bytes(b'not an installer')
            canonical = f'https://github.com/PlotnikovDL/tjcloud-client/releases/download/v{version}/' + fixture.name
            cases = [(version, canonical), ('0.1.1', canonical), ('01.1.0', canonical),
                     (version, canonical.replace('https:', 'http:')),
                     (version, canonical + '?token=synthetic'),
                     (version, canonical.replace('github.com', 'github.com.example.org'))]
            for index, (version, url) in enumerate(cases):
                with self.subTest(version=version, url=url):
                    output = Path(folder) / str(index)
                    result = run_ps_file(script, '-InstallerPath', str(fixture), '-Version', version,
                                         '-InstallerUrl', url, '-OutputDirectory', str(output))
                    self.assertNotEqual(0, result.returncode)
                    self.assertFalse(output.exists())
    def test_distribution_carries_third_party_license_files(self):
        build = (ROOT / 'scripts' / 'Build.ps1').read_text(encoding='utf-8')
        package = (ROOT / 'scripts' / 'Package.ps1').read_text(encoding='utf-8')
        for name in ['THIRD_PARTY_NOTICES.md', 'licenses']:
            self.assertIn(name, build)
            self.assertIn(name, package)

    def test_installer_acceptance_refuses_non_ephemeral_host(self):
        script = ROOT / 'scripts' / 'Test-Installer.ps1'
        self.assertTrue(script.is_file(), 'Runner-only installer acceptance is required')
        environment = dict(os.environ, GITHUB_ACTIONS='false')
        result = run_ps_file(script, '-SetupPath', 'nonexistent.exe', '-PublishDirectory', 'nonexistent', env=environment)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Only an ephemeral GitHub-hosted Windows runner', result.stdout + result.stderr)
        text = script.read_text(encoding='utf-8')
        for expected in ['RUNNER_ENVIRONMENT', '/NOICONS', '/DIR=', 'Test-Smoke.ps1',
                         'DisplayName', 'DisplayVersion', 'Publisher', 'Get-FileHash', 'unins000.exe']:
            self.assertIn(expected, text)
    def test_ci_uses_pinned_actions_and_separates_release_permissions(self):
        ci_path = ROOT / '.github/workflows/ci.yml'
        release_path = ROOT / '.github/workflows/release.yml'
        self.assertTrue(ci_path.is_file(), 'CI workflow is required')
        self.assertTrue(release_path.is_file(), 'Tag-only release workflow is required')
        ci = ci_path.read_text(encoding='utf-8')
        release = release_path.read_text(encoding='utf-8')
        for expected in ['pull_request:', 'workflow_call:', 'contents: read', 'windows-2025',
                         'Build.ps1', 'Get-InnoCompiler.ps1', 'Package.ps1', 'Test-Installer.ps1', 'upload-artifact@']:
            self.assertIn(expected, ci)
        self.assertNotIn('contents: write', ci)
        self.assertNotIn('pull_request_target', ci + release)
        self.assertIn('contents: write', release)
        self.assertIn("tags: ['v*']", release)
        self.assertIn('refs/tags/v', release)
        self.assertIn('attest-build-provenance@', release)
        for action in re.findall(r'uses:\s*([^\s#]+)', ci + release):
            self.assertTrue(action.startswith('./') or re.fullmatch(r'actions/[a-z-]+@[0-9a-f]{40}', action), action)
    def test_portable_compiler_rejects_unverified_input_before_extraction(self):
        script = ROOT / 'scripts' / 'Get-InnoCompiler.ps1'
        self.assertTrue(script.is_file(), 'Verified portable compiler helper is required')
        with tempfile.TemporaryDirectory(prefix='.packaging-tests-', dir=ROOT) as folder:
            archive = Path(folder) / 'innosetup-6.7.4.exe'
            archive.write_bytes(b'synthetic invalid tool archive')
            output = Path(folder) / 'compiler'
            result = run_ps_file(script, '-ArchivePath', str(archive), '-ToolRoot', str(output))
            self.assertNotEqual(0, result.returncode)
            self.assertIn('Compiler archive SHA-256 mismatch', result.stdout + result.stderr)
            self.assertFalse(output.exists())
        text = script.read_text(encoding='utf-8')
        for expected in ['Get-AuthenticodeSignature', "'Valid'", 'Pyrsys B.V.',
                         'verify-asset', '/PORTABLE=1', '/CURRENTUSER', '/NOICONS']:
            self.assertIn(expected, text)


def run_ps_file(script, *args, env=None):
    shell = shutil.which('pwsh') or shutil.which('powershell')
    if not shell:
        shell = 'C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe'
    return subprocess.run([shell, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(script), *args],
                          cwd=ROOT, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=30, env=env)


if __name__ == '__main__':
    unittest.main()
