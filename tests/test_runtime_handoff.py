from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


class RuntimeHandoffContract(unittest.TestCase):
    def test_verified_installer_never_uses_netsparkle_cmd(self):
        source = (ROOT / 'TjClient.Updates/SignedUpdateBackend.cs').read_text(encoding='utf-8-sig')
        self.assertNotIn('base.RunDownloadedInstaller(', source,
                         'NetSparkle 3.1 CMD expands local !/% paths before installer launch')
        self.assertIn('UseShellExecute = false', source)
        self.assertIn('ArgumentList.Add', source)
        self.assertIn('/WAITPID=', source)
        self.assertIn('/WAITSTART=', source)
        self.assertIn('await sparkle.InstallUpdate(item, verified)', source)

    def test_inno_waits_for_exact_parent_before_copy(self):
        source = (ROOT / 'packaging/TjCloudClient.iss').read_text(encoding='utf-8-sig')
        self.assertIn('function PrepareToInstall(var NeedsRestart: Boolean): String;', source)
        self.assertIn('OpenProcess', source)
        self.assertIn('GetProcessTimes', source)
        self.assertIn('WaitForSingleObject', source)
        self.assertIn('CloseHandle', source)
        self.assertNotIn('TerminateProcess', source)
        self.assertIn('/WAITPID=', source)
        self.assertIn('/WAITSTART=', source)
        self.assertIn('60000', source)


if __name__ == '__main__':
    unittest.main()
