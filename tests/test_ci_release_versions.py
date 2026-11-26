import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class ReleaseVersionCi(unittest.TestCase):
    def test_ci_runs_real_version_regression_with_all_fixtures(self):
        ci = (ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')
        for value in ('RELEASE_TEST_TOOLS_DLL', 'RELEASE_TEST_SETUP_1_0_0',
                      'RELEASE_TEST_SETUP_1_0_1', 'RELEASE_TEST_DOTNET',
                      'test_release_versions.py', 'Release version regression failed'):
            self.assertIn(value, ci)
        self.assertIn("-Version '1.0.0'", ci)
        self.assertIn("-Version '1.0.1'", ci)


if __name__ == '__main__':
    unittest.main()
