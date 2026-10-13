import hashlib
import importlib.util
import io
import pathlib
import tempfile
import unittest
from unittest.mock import patch

path = pathlib.Path(__file__).resolve().parents[2] / "BEefyER" / "prepare-models.py"
spec = importlib.util.spec_from_file_location("beefyer_prepare_models", path)
models = importlib.util.module_from_spec(spec)
spec.loader.exec_module(models)


class ModelPreparationTests(unittest.TestCase):
    def test_vad_download_is_verified_and_cached(self):
        data = b"verified fixture model"
        with tempfile.TemporaryDirectory() as folder, patch.object(models, "ROOT", pathlib.Path(folder)), \
                patch.object(models, "VAD_SHA256", hashlib.sha256(data).hexdigest()), \
                patch.object(models.urllib.request, "urlopen", return_value=io.BytesIO(data)) as download:
            target = models.install_vad()
            self.assertEqual(target.read_bytes(), data)
            self.assertEqual(models.install_vad(), target)
            self.assertEqual(download.call_count, 1)

    def test_bad_vad_checksum_never_publishes_model(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(models, "ROOT", pathlib.Path(folder)), \
                patch.object(models.urllib.request, "urlopen", return_value=io.BytesIO(b"corrupt")):
            with self.assertRaises(RuntimeError):
                models.install_vad()
            self.assertFalse((pathlib.Path(folder) / "models" / "silero-v6.2.1.onnx").exists())

    def test_f16_conversion_uses_pinned_source_cpu_dependencies_and_atomic_output(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(models, "ROOT", pathlib.Path(folder)):
            native = pathlib.Path(folder) / "native"
            native.mkdir()
            (native / "convert_model.py").write_text("# fixture")
            calls = []
            def run(argv, **kwargs):
                calls.append(argv)
                if "--outfile" in argv:
                    pathlib.Path(argv[argv.index("--outfile") + 1]).write_bytes(b"GGUFfixture")
            with patch.object(models.subprocess, "check_output", side_effect=[models.RUNTIME_REVISION, "3.12"]), \
                    patch.object(models.subprocess, "run", side_effect=run):
                target = models.install_f16("/fixture/python3.12")
            conversion = calls[-1]
            self.assertIn(models.ASR_REVISION, conversion)
            self.assertEqual(conversion[conversion.index("--outtype") + 1], "f16")
            self.assertIn("https://download.pytorch.org/whl/cpu", calls[1])
            self.assertEqual(target.read_bytes(), b"GGUFfixture")
            self.assertFalse(target.with_suffix(".partial").exists())

    def test_python314_is_rejected_for_converter_without_changing_worker_environment(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(models, "ROOT", pathlib.Path(folder)):
            native = pathlib.Path(folder) / "native"
            native.mkdir()
            (native / "convert_model.py").write_text("# fixture")
            with patch.object(models.subprocess, "check_output", side_effect=[models.RUNTIME_REVISION, "3.14"]), \
                    patch.object(models.subprocess, "run") as run:
                with self.assertRaises(RuntimeError):
                    models.install_f16("/fixture/python3.14")
                run.assert_not_called()

    def test_python314_host_can_bootstrap_managed_python312_for_conversion(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(models, "ROOT", pathlib.Path(folder)):
            native = pathlib.Path(folder) / "native"
            native.mkdir()
            (native / "convert_model.py").write_text("# fixture")
            calls = []
            def run(argv, **kwargs):
                calls.append((argv, kwargs))
                if "--outfile" in argv:
                    pathlib.Path(argv[argv.index("--outfile") + 1]).write_bytes(b"GGUFfixture")
            with patch.object(models.subprocess, "check_output", return_value=models.RUNTIME_REVISION), \
                    patch.object(models.shutil, "which", return_value=None), \
                    patch.object(models.subprocess, "run", side_effect=run):
                models.install_f16(None)
            uv_call = next((argv, kwargs) for argv, kwargs in calls if argv[0].endswith("/uv"))
            self.assertEqual(uv_call[0][uv_call[0].index("--python") + 1], "3.12")
            self.assertTrue(uv_call[1]["env"]["UV_PYTHON_INSTALL_DIR"].startswith(folder))
            self.assertTrue(any(f"uv=={models.UV_VERSION}" in argv for argv, _ in calls))

    def test_repeat_conversion_reuses_managed_environment(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(models, "ROOT", pathlib.Path(folder)):
            root = pathlib.Path(folder)
            (root / "native").mkdir()
            (root / "native" / "convert_model.py").write_text("# fixture")
            (root / "converter-venv" / "bin").mkdir(parents=True)
            (root / "converter-venv" / "bin" / "python").write_text("# fixture")
            calls = []
            def run(argv, **kwargs):
                calls.append(argv)
                if "--outfile" in argv:
                    pathlib.Path(argv[argv.index("--outfile") + 1]).write_bytes(b"GGUFfixture")
            with patch.object(models.subprocess, "check_output", return_value=models.RUNTIME_REVISION), \
                    patch.object(models.shutil, "which", return_value=None), \
                    patch.object(models.subprocess, "run", side_effect=run):
                models.install_f16(None)
            self.assertFalse(any(argv[0].endswith("/uv") for argv in calls))


if __name__ == "__main__":
    unittest.main()
