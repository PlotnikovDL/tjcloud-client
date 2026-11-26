import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'scripts/Test-BranchPolicy.ps1'
REPOSITORY = 'example/client'


class BranchPolicyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='tj-policy-')
        self.addCleanup(self.temporary.cleanup)
        self.work = Path(self.temporary.name)

    def policy(self, event_name='pull_request', base='develop', head='feature/test',
               head_repo=REPOSITORY, ref='refs/pull/1/merge'):
        self.assertTrue(SCRIPT.is_file(), 'Branch policy script is required')
        payload = {'pull_request': {'base': {'ref': base},
                                   'head': {'ref': head, 'repo': {'full_name': head_repo}}}}
        event = self.work / 'event.json'
        event.write_text(json.dumps(payload), encoding='utf-8')
        shell = shutil.which('pwsh') or shutil.which('powershell')
        shell = shell or 'C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe'
        return subprocess.run([shell, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(SCRIPT),
                               '-RepositoryRoot', str(self.work), '-EventName', event_name,
                               '-EventPath', str(event), '-Repository', REPOSITORY, '-Ref', ref],
                              capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=30)

    def assert_allowed(self, **kwargs):
        result = self.policy(**kwargs)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn('Branch policy passed.', result.stdout)

    def assert_denied(self, reason, **kwargs):
        result = self.policy(**kwargs)
        self.assertNotEqual(0, result.returncode)
        self.assertIn(reason, result.stdout + result.stderr)

    def test_develop_accepts_feature_branch(self):
        self.assert_allowed()

    def test_develop_rejects_non_feature_head(self):
        for head in ['master', 'develop', 'fix/test', 'feature/', 'Feature/test']:
            with self.subTest(head=head):
                self.assert_denied('develop requires a feature/* head.', head=head)

    def test_develop_accepts_fork_feature_branch(self):
        self.assert_allowed(head_repo='contributor/client')

    def test_master_requires_same_repository_develop(self):
        self.assert_allowed(base='master', head='develop')
        for head, repository in [('feature/test', REPOSITORY), ('develop', 'fork/client'),
                                 ('develop', None), ('Develop', REPOSITORY)]:
            with self.subTest(head=head, repository=repository):
                self.assert_denied('master requires develop from this repository.',
                                   base='master', head=head, head_repo=repository)

    def test_unknown_pr_base_is_rejected(self):
        self.assert_denied('Unsupported PR base.', base='release')

    def test_tag_must_exactly_match_central_version(self):
        (self.work / 'Directory.Build.props').write_text(
            '<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>', encoding='utf-8')
        for ref in ['refs/tags/v0.1.0', 'refs/tags/v1.0.1', 'refs/tags/v01.0.0', 'refs/tags/1.0.0']:
            with self.subTest(ref=ref):
                self.assert_denied('Release tag does not match Directory.Build.props.',
                                   event_name='push', ref=ref)

    def test_push_branch_does_not_require_pr_metadata(self):
        self.assert_allowed(event_name='push', ref='refs/heads/feature/test')

    def git(self, *args, input=None):
        environment = dict(os.environ, GIT_AUTHOR_NAME='Synthetic', GIT_AUTHOR_EMAIL='test@example.org',
                           GIT_COMMITTER_NAME='Synthetic', GIT_COMMITTER_EMAIL='test@example.org')
        return subprocess.run(['git', '-C', str(self.work), *args], input=input, capture_output=True,
                              text=True, check=True, env=environment, timeout=15).stdout.strip()

    def history(self):
        self.git('init', '--quiet')
        tree = self.git('mktree', input='')
        root = self.git('commit-tree', tree, '-m', 'synthetic root')
        master = self.git('commit-tree', tree, '-p', root, '-m', 'synthetic master')
        side = self.git('commit-tree', tree, '-p', root, '-m', 'synthetic side')
        self.git('update-ref', 'refs/remotes/origin/master', master)
        self.git('update-ref', 'HEAD', master)
        (self.work / 'Directory.Build.props').write_text(
            '<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>', encoding='utf-8')
        return root, master, side

    def test_tag_requires_real_master_ancestry_not_head(self):
        root, master, side = self.history()
        self.git('update-ref', 'refs/tags/v1.0.0', side)
        self.assert_denied('Release tag is not reachable from origin/master.',
                           event_name='push', ref='refs/tags/v1.0.0')
        for commit in [root, master]:
            self.git('update-ref', 'refs/tags/v1.0.0', commit)
            self.assert_allowed(event_name='push', ref='refs/tags/v1.0.0')

    def test_tag_fails_closed_without_master_or_tag(self):
        root, master, side = self.history()
        self.assert_denied('Cannot resolve release tag.', event_name='push', ref='refs/tags/v1.0.0')
        self.git('update-ref', 'refs/tags/v1.0.0', master)
        self.git('update-ref', '-d', 'refs/remotes/origin/master')
        self.assert_denied('Cannot resolve origin/master.', event_name='push', ref='refs/tags/v1.0.0')

    def test_annotated_tag_uses_its_commit(self):
        root, master, side = self.history()
        self.git('tag', '-a', 'v1.0.0', root, '-m', 'synthetic release')
        self.assert_allowed(event_name='push', ref='refs/tags/v1.0.0')


class WorkflowPolicyContracts(unittest.TestCase):
    def test_policy_job_is_required_before_build(self):
        ci = (ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')
        self.assertIn('  policy:\n    name: policy\n', ci)
        self.assertIn('  build:\n    needs: policy\n', ci)
        self.assertIn('fetch-depth: 0', ci)
        self.assertIn('run: ./scripts/Test-BranchPolicy.ps1', ci)
        self.assertIn('types: [opened, synchronize, reopened, edited]', ci)
        for trigger in ['push:', 'pull_request:', 'workflow_call:']:
            self.assertIn(trigger, ci)
        self.assertNotIn('secrets.', ci)
        self.assertNotIn('write', ci)
        self.assertNotIn('pull_request_target', ci)

    def test_node24_actions_are_pinned_to_verified_commits(self):
        ci = (ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')
        release = (ROOT / '.github/workflows/release.yml').read_text(encoding='utf-8')
        self.assertIn('actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1', ci)
        self.assertIn('actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9', ci)
        self.assertIn('actions/download-artifact@9000827ccba6bdab643e8b6fd33ac0654aef8333', release)
        self.assertEqual(2, ci.count('archive: true'))
        self.assertIn('digest-mismatch: error', release)
        self.assertIn("github.event_name == 'push'", release)


if __name__ == '__main__':
    unittest.main()
