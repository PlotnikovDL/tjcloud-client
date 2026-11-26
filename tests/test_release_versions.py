import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from xml.etree import ElementTree

from tests.test_distribution import ROOT, run_ps_file


class ReleaseVersions(unittest.TestCase):
    def run_signing(self, version, setup_version):
        dll = os.environ.get('RELEASE_TEST_TOOLS_DLL')
        setup = os.environ.get('RELEASE_TEST_SETUP_' + setup_version.replace('.', '_'))
        if not dll or not setup:
            self.skipTest('Supply RELEASE_TEST_TOOLS_DLL and real Inno RELEASE_TEST_SETUP_<version> fixtures')
        with tempfile.TemporaryDirectory(prefix='release-versions-') as folder:
            root = Path(folder)
            (root / 'scripts').mkdir()
            (root / 'packaging').mkdir()
            for name in ('Test-ReleaseSigning.ps1', 'Common.ps1'):
                shutil.copyfile(ROOT / 'scripts' / name, root / 'scripts' / name)
            shutil.copyfile(ROOT / 'packaging/TjCloudClient.iss', root / 'packaging/TjCloudClient.iss')
            props = ElementTree.parse(ROOT / 'Directory.Build.props')
            props.find('./PropertyGroup/Version').text = version
            props.write(root / 'Directory.Build.props', encoding='utf-8')
            result = run_ps_file(root / 'scripts/Test-ReleaseSigning.ps1',
                                 '-SetupPath', setup, '-VerifierDll', dll,
                                 '-WorkRoot', str(root / 'fixtures'),
                                 '-DotNet', os.environ.get('RELEASE_TEST_DOTNET', 'dotnet'))
            return result

    def test_actual_inno_current_version(self):
        result = self.run_signing('1.0.0', '1.0.0')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn('RESULT release-tests=20 failed=0', result.stdout)

    def test_actual_inno_next_version_from_central_metadata(self):
        result = self.run_signing('1.0.1', '1.0.1')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn('RESULT release-tests=20 failed=0', result.stdout)

    def test_actual_inno_product_version_mismatch_is_rejected(self):
        result = self.run_signing('1.0.0', '1.0.1')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Installer ProductVersion does not match the numeric release version.',
                      result.stdout + result.stderr)

    def test_explicit_numeric_version_is_required_before_fixture_creation(self):
        dll = os.environ.get('RELEASE_TEST_TOOLS_DLL')
        if not dll:
            self.skipTest('Supply RELEASE_TEST_TOOLS_DLL to execute numeric version rejection')
        dotnet = os.environ.get('RELEASE_TEST_DOTNET', 'dotnet')
        for version in ('v1.0.1', '01.0.1', '1.0', '1.0.1.0', '1.0.1\n', '../1.0.1'):
            with self.subTest(version=version), tempfile.TemporaryDirectory(prefix='release-version-invalid-') as folder:
                root = Path(folder) / 'fixtures'
                result = subprocess.run([dotnet, dll, 'self-test', 'nonexistent.exe', str(root), version],
                                        capture_output=True, text=True, timeout=30,
                                        env=dict(os.environ, DOTNET_GENERATE_ASPNET_CERTIFICATE='false',
                                                 DOTNET_CLI_TELEMETRY_OPTOUT='1'))
                self.assertNotEqual(0, result.returncode)
                self.assertIn('Invalid release version.', result.stdout + result.stderr)
                self.assertFalse(root.exists())

    def test_generator_version_contract_keeps_host_guard_and_wrong_fallback(self):
        text = (ROOT / 'tools/GeneratorTests.cs').read_text(encoding='utf-8')
        self.assertIn('Run(string generator, string setup, string root, string version)', text)
        self.assertNotIn('const string version', text)
        self.assertNotIn('/releases/download/v1.0.0', text)
        self.assertIn('repository + "/releases/download/v" + version', text)
        self.assertIn('"--file-version", "9.9.9"', text)
        self.assertIn('RUNNER_ENVIRONMENT', text)
        self.assertLess(text.index('RUNNER_ENVIRONMENT'), text.index('Directory.CreateDirectory(root)'))
        self.assertIn(r'\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z', text)
        caller = (ROOT / 'scripts/Test-ReleaseSigning.ps1').read_text(encoding='utf-8')
        self.assertIn('Get-PackageMetadata', caller)
        for line in caller.splitlines():
            if '& $DotNet $VerifierDll' in line:
                self.assertTrue(line.endswith('$metadata.Version'), line)


if __name__ == '__main__':
    unittest.main()
