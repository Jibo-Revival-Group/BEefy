#!/usr/bin/env python3
"""Install a pinned compact contextual masked LM and isolated CPU runtime."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import urllib.request
import venv

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


def environment(directory):
    python = directory / (
        "Scripts/python.exe" if sys.platform == "win32" else "bin/python"
    )
    if not python.exists():
        venv.EnvBuilder(with_pip=True).create(directory)
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
    args = parser.parse_args()
    root = args.directory.resolve()
    root.mkdir(parents=True, exist_ok=True)
    if not args.model_only:
        python = environment(root / "venv")
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
        exporter = environment(root / "export-venv")
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
    main()
