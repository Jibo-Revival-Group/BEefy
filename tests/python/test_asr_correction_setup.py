"""Offline setup regressions: no models or dependency downloads required."""
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[2] / 'scripts/cloud/setup-asr-correction-model.py'
spec = importlib.util.spec_from_file_location('asr_setup', SCRIPT)
setup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(setup)


def result(code=0, stdout='', stderr=''):
    return subprocess.CompletedProcess([], code, stdout, stderr)


class SetupTests(unittest.TestCase):
    def test_rejects_explicit_python314_before_creating_assets(self):
        with patch.object(setup, 'python_version', return_value=(3, 14)):
            with self.assertRaisesRegex(RuntimeError, 'Python 3.14 is not supported'):
                setup.compatible_python('/explicit/python')

    def test_autodetects_compatible_interpreter_when_host_is314(self):
        with patch.object(setup.sys, 'executable', '/host/python'), \
             patch.object(setup.shutil, 'which', side_effect=lambda name: '/bin/' + name), \
             patch.object(setup, 'python_version', side_effect=[(3, 14), (3, 12)]):
            self.assertEqual('/bin/python3.12', setup.compatible_python())

    def test_healthy_environment_does_not_bootstrap_or_recreate(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root) / 'venv'
            python = directory / ('Scripts/python.exe' if sys.platform == 'win32' else 'bin/python')
            python.parent.mkdir(parents=True)
            python.touch()
            with patch.object(setup, 'python_version', return_value=(3, 12)), \
                 patch.object(setup.subprocess, 'run', return_value=result()) as run:
                self.assertEqual(python, setup.environment(directory, '/base/python'))
                run.assert_called_once_with([str(python), '-m', 'pip', '--version'], capture_output=True, text=True)

    def test_preserves_incompatible_environment_before_recreating(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root) / 'venv'
            python = directory / ('Scripts/python.exe' if sys.platform == 'win32' else 'bin/python')
            python.parent.mkdir(parents=True)
            python.touch()
            marker = directory / 'original.txt'
            marker.write_text('preserve me')
            with patch.object(setup, 'python_version', side_effect=[(3, 12), (3, 14)]), \
                 patch.object(setup.subprocess, 'run', return_value=result()) as run:
                setup.environment(directory, '/base/python3.12')
                self.assertEqual('preserve me', (Path(root) / 'venv.previous/original.txt').read_text())
                self.assertEqual(['/base/python3.12', '-m', 'venv', str(directory)], run.call_args_list[0].args[0])

    def test_missing_ensurepip_reports_matching_ubuntu_package(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root) / 'venv'
            python = directory / ('Scripts/python.exe' if sys.platform == 'win32' else 'bin/python')
            python.parent.mkdir(parents=True)
            python.touch()
            with patch.object(setup, 'python_version', return_value=(3, 12)), \
                 patch.object(setup.subprocess, 'run', side_effect=[result(1), result(1, stderr='No module named ensurepip')]):
                with self.assertRaisesRegex(RuntimeError, 'Install python3.12-venv'):
                    setup.environment(directory, '/base/python')

    def test_real_pipless_environment_is_repaired_without_downloads(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root) / 'venv'
            subprocess.run([sys.executable, '-m', 'venv', '--without-pip', str(directory)], check=True)
            python = directory / ('Scripts/python.exe' if sys.platform == 'win32' else 'bin/python')
            self.assertNotEqual(0, subprocess.run([str(python), '-m', 'pip', '--version'], capture_output=True).returncode)
            repaired = setup.environment(directory, sys.executable)
            self.assertEqual(python, repaired)
            self.assertEqual(0, subprocess.run([str(repaired), '-m', 'pip', '--version'], capture_output=True).returncode)


if __name__ == '__main__':
    unittest.main()
