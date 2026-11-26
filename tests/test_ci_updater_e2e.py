import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class UpdaterCi(unittest.TestCase):
    def test_hosted_client_update_is_required_before_distribution_upload(self):
        ci = (ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')
        self.assertIn('./scripts/Test-AutoUpdateE2E.ps1', ci, 'CI must execute the hosted client update harness')
        self.assertLess(ci.index('./scripts/Test-AutoUpdateE2E.ps1'), ci.index('name: Upload verified distribution'))
        self.assertIn('${{ runner.temp }}/updater-e2e/hosted-result.json', ci)
        self.assertIn('${{ runner.temp }}/updater-e2e/*/*.json', ci)

    def test_ci_retains_updater_source_manifest(self):
        ci = (ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')
        self.assertTrue('${{ runner.temp }}/updater-e2e/source-manifest.json' in ci,
                        'CI evidence must retain the source hashes used by updater E2E')


if __name__ == '__main__':
    unittest.main()
