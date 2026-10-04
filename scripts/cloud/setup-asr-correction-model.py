#!/usr/bin/env python3
"""Install a pinned compact contextual masked LM and isolated CPU runtime."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import shutil
import urllib.request

MODEL_ID = "prajjwal1/bert-mini"
REVISION = "5e123abc2480f0c4b4cac186d3b3f09299c258fc"
DICTIONARY_REVISION = "74790861f652b15e4ac49015a90074ad62a27690"
CHECKPOINTS = {
    "config.json": "d32ac9faf7e47097bea0395fd2e0cc8afc9ce038ad7fa41bdffa37386972b524",
    "pytorch_model.bin": "f7902e759e678cf77852a40a710e79bb83acb475c44c177773246d610818a5db",
    "vocab.txt": "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
}


def download(url, target, expected):
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists() and hashlib.sha256(target.read_bytes()).hexdigest() == expected:
        return
    temporary = target.with_suffix(target.suffix + ".download")
    print(f"Downloading {target.name}", flush=True)
    with urllib.request.urlopen(url, timeout=120) as source, temporary.open(
        "wb"
    ) as output:
        while chunk := source.read(1024 * 1024):
            output.write(chunk)
    if hashlib.sha256(temporary.read_bytes()).hexdigest() != expected:
        temporary.unlink()
        raise RuntimeError(f"Checksum mismatch: {target.name}")
    temporary.replace(target)


def python_version(python):
    result = subprocess.run(
        [str(python), "-c", "import json,sys; print(json.dumps(list(sys.version_info[:2])))"],
        capture_output=True, text=True, check=True,
    )
    return tuple(json.loads(result.stdout))


def compatible_python(configured=None):
    candidates = [configured] if configured else [
        sys.executable, *(shutil.which(f"python3.{minor}") for minor in (12, 13, 11, 10))
    ]
    for candidate in dict.fromkeys(path for path in candidates if path):
        try:
            version = python_version(candidate)
        except (OSError, ValueError, subprocess.CalledProcessError):
            continue
        if (3, 10) <= version <= (3, 13):
            return str(candidate)
    raise RuntimeError(
        "The pinned ASR packages require Python 3.10–3.13 (Python 3.12 recommended). "
        "Install a compatible interpreter with its venv/ensurepip support, then retry "
        "with --python /path/to/python3.12. Python 3.14 is not supported by these pins."
    )


def environment(directory, interpreter):
    python = directory / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")
    selected_version = python_version(interpreter)
    if python.exists():
        try:
            existing_version = python_version(python)
        except (OSError, ValueError, subprocess.CalledProcessError):
            existing_version = None
        if existing_version != selected_version:
            # Preserve existing environments rather than rewriting their interpreter.
            backup = directory.with_name(directory.name + ".previous")
            number = 1
            while backup.exists():
                backup = directory.with_name(f"{directory.name}.previous-{number}")
                number += 1
            directory.rename(backup)
            print(f"Preserved incompatible environment at {backup}", flush=True)
    if not python.exists():
        result = subprocess.run(
            [str(interpreter), "-m", "venv", str(directory)],
            capture_output=True, text=True,
        )
        if result.returncode:
            raise RuntimeError(
                f"Could not create {directory} with {interpreter}. Install the matching "
                f"Python {selected_version[0]}.{selected_version[1]} venv package "
                f"(on Ubuntu: python{selected_version[0]}.{selected_version[1]}-venv). "
                + (result.stderr or result.stdout).strip()
            )
    check = subprocess.run(
        [str(python), "-m", "pip", "--version"], capture_output=True, text=True,
    )
    if check.returncode:
        print(f"Bootstrapping missing pip in {directory}", flush=True)
        bootstrap = subprocess.run(
            [str(python), "-m", "ensurepip", "--upgrade", "--default-pip"],
            capture_output=True, text=True,
        )
        if bootstrap.returncode:
            raise RuntimeError(
                f"pip is missing in {directory} and ensurepip could not repair it. "
                f"Install python{selected_version[0]}.{selected_version[1]}-venv "
                "on Ubuntu, then rerun setup. " + (bootstrap.stderr or bootstrap.stdout).strip()
            )
        subprocess.run([str(python), "-m", "pip", "--version"], check=True)
    return python


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--directory",
        type=Path,
        default=Path(__file__).resolve().parents[2] / "App_Data/asr-correction",
    )
    parser.add_argument(
        "--model-only",
        action="store_true",
        help="Skip installing the inference runtime",
    )
    parser.add_argument(
        "--python", dest="python_path",
        help="Python 3.10–3.13 interpreter for both virtual environments (auto-detected by default)",
    )
    args = parser.parse_args()
    interpreter = compatible_python(args.python_path)
    print(f"Using compatible Python: {interpreter}", flush=True)
    root = args.directory.resolve()
    root.mkdir(parents=True, exist_ok=True)
    if not args.model_only:
        python = environment(root / "venv", interpreter)
        subprocess.run(
            [
                str(python),
                "-m",
                "pip",
                "install",
                "onnxruntime==1.23.2",
                "tokenizers==0.22.1",
                "numpy==2.2.6",
            ],
            check=True,
        )
    model = root / "model"
    model.mkdir(exist_ok=True)
    download(
        "https://huggingface.co/onnx-community/bert-mini-ONNX/resolve/73f66743176d942fa4d8eced57de6d99668261fe/tokenizer.json",
        model / "tokenizer.json",
        "d241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66",
    )
    for name, digest in {
        "cmudict.dict": "81917843c7f44ce2b094ac63873c2c7a4cf802040792c455ba3ca406891c3d22",
        "CMUDICT-LICENSE": "bd4ce8e44170a5f9f481310ca85c51de3c4f851a65e679b40e603b143bd3542a",
    }.items():
        filename = "LICENSE" if name == "CMUDICT-LICENSE" else name
        download(
            f"https://raw.githubusercontent.com/cmusphinx/cmudict/{DICTIONARY_REVISION}/{filename}",
            model / name,
            digest,
        )
    manifest = model / "manifest.json"
    existing = json.loads(manifest.read_text()) if manifest.exists() else {}
    weights = model / "onnx/model_quantized.onnx"
    if (
        existing.get("model") != MODEL_ID
        or existing.get("revision") != REVISION
        or not weights.exists()
        or hashlib.sha256(weights.read_bytes()).hexdigest()
        != existing.get("sha256", {}).get("onnx/model_quantized.onnx")
    ):
        for name, digest in CHECKPOINTS.items():
            download(
                f"https://huggingface.co/{MODEL_ID}/resolve/{REVISION}/{name}",
                root / "source" / name,
                digest,
            )
        exporter = environment(root / "export-venv", interpreter)
        subprocess.run(
            [
                str(exporter),
                "-m",
                "pip",
                "install",
                "torch==2.14.1",
                "--index-url",
                "https://download.pytorch.org/whl/cpu",
            ],
            check=True,
        )
        subprocess.run(
            [
                str(exporter),
                "-m",
                "pip",
                "install",
                "transformers==4.49.0",
                "onnx==1.20.1",
                "numpy==2.2.6",
                "onnxruntime==1.23.2",
            ],
            check=True,
        )
        subprocess.run(
            [
                str(exporter),
                str(Path(__file__).with_name("export-asr-context-model.py")),
                "--directory",
                str(root),
            ],
            check=True,
        )
    files = [
        "onnx/model_quantized.onnx",
        "config.json",
        "tokenizer.json",
        "cmudict.dict",
        "CMUDICT-LICENSE",
    ]
    manifest.write_text(
        json.dumps(
            {
                "model": MODEL_ID,
                "revision": REVISION,
                "dictionaryRevision": DICTIONARY_REVISION,
                "sha256": {
                    name: hashlib.sha256((model / name).read_bytes()).hexdigest()
                    for name in files
                },
            },
            indent=2,
        )
        + "\n"
    )
    print(f"Installed contextual model: {model}")
    print(
        "source and export-venv are only needed to rebuild weights; inference uses model and venv."
    )


if __name__ == "__main__":
    try:
        main()
    except RuntimeError as error:
        sys.exit(f"ASR setup failed: {error}")
