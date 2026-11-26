import unittest
import os
from pathlib import Path
from tests.test_distribution import run_ps_file

ROOT = Path(__file__).resolve().parents[1]


class SigningContracts(unittest.TestCase):
    def test_production_signer_refuses_user_host_before_reading_keys(self):
        result = run_ps_file(ROOT / 'scripts/New-ReleaseFeed.ps1',
                             '-InputDirectory', 'nonexistent', '-OutputDirectory', 'nonexistent',
                             '-GeneratorPath', 'nonexistent', '-VerifierDll', 'nonexistent',
                             env=dict(os.environ, GITHUB_ACTIONS='false'))
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Production signing requires an approved hosted release environment.', result.stdout + result.stderr)

    def test_signing_uses_protected_environment_and_independent_verifier(self):
        script = ROOT / 'scripts/New-ReleaseFeed.ps1'
        self.assertTrue(script.is_file(), 'Protected release signer is required')
        text = script.read_text(encoding='utf-8')
        for expected in ['SPARKLE_PRIVATE_KEY', 'SPARKLE_PUBLIC_KEY', '--single-file', '--file-version',
                         'windows-x64', '--base-url', '--appcast-output-directory', 'ReleaseTools',
                         'GITHUB_ACTIONS', 'GITHUB_REF', '2.9.0']:
            self.assertIn(expected, text)
        self.assertNotIn('--export', text)
        self.assertNotIn('--private-key-override', text)

    def test_release_secret_is_scoped_to_environment_signing_step(self):
        release = (ROOT / '.github/workflows/release.yml').read_text(encoding='utf-8')
        ci = (ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')
        self.assertIn('environment: release', release)
        self.assertIn('SPARKLE_PRIVATE_KEY: ${{ secrets.SPARKLE_PRIVATE_KEY }}', release)
        self.assertIn('New-ReleaseFeed.ps1', release)
        self.assertIn('Test-ReleaseAssets.ps1', release)
        self.assertIn('appcast.xml.signature', release)
        self.assertNotIn('secrets.', ci)
        self.assertIn('Test-ReleaseSigning.ps1', ci)


if __name__ == '__main__':
    unittest.main()
