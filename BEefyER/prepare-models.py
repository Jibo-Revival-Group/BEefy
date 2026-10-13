#!/usr/bin/env python3
"""Install pinned VAD assets and optionally convert the English ASR model to F16."""
import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import sys
import urllib.request

ROOT = Path(__file__).resolve().parent
VAD_URL = "https://raw.githubusercontent.com/snakers4/silero-vad/7e30209a3e901f9842f81b225f3e93d8199902b1/src/silero_vad/data/silero_vad.onnx"
VAD_SHA256 = "1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3"
ASR_REVISION = "ebe59e5a817142986528bbbee5dba8db7b38ed50"
RUNTIME_REVISION = "e59dfb0b5c22655620f41391cc9a000d6f56b802"
UV_VERSION = "0.13.0"


def install_vad():
    target = ROOT / "models" / "silero-v6.2.1.onnx"
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists() and hashlib.sha256(target.read_bytes()).hexdigest() == VAD_SHA256:
        return target
    with urllib.request.urlopen(VAD_URL, timeout=60) as response:
        data = response.read(4 * 1024 * 1024)
    if hashlib.sha256(data).hexdigest() != VAD_SHA256:
        raise RuntimeError("Silero VAD checksum mismatch; refusing model installation")
    temporary = target.with_suffix(".partial")
    temporary.write_bytes(data)
    temporary.replace(target)
    return target


def install_f16(python):
    native = ROOT / "native"
    if not (native / "convert_model.py").is_file():
        raise RuntimeError("Run build-runtime.sh first; the pinned converter source is required")
    revision = subprocess.check_output(["git", "-C", str(native), "rev-parse", "HEAD"], text=True).strip()
    if revision != RUNTIME_REVISION:
        raise RuntimeError("Native converter revision differs from the supported pinned runtime")
    if python is None:
        python = shutil.which("python3.12") or shutil.which("python3.11")
    env_dir = ROOT / "converter-venv"
    if python is not None:
        version = subprocess.check_output([python, "-c", "import sys; print('%d.%d' % sys.version_info[:2])"], text=True).strip()
        if version not in {"3.11", "3.12"}:
            raise RuntimeError("The pinned native converter requires Python 3.11 or 3.12, separate from the worker environment")
        subprocess.run([python, "-m", "venv", str(env_dir)], check=True)
    elif not (env_dir / "bin" / "python").is_file():
        tools = ROOT / "converter-tools"
        subprocess.run([sys.executable, "-m", "venv", str(tools)], check=True)
        subprocess.run([str(tools / "bin" / "python"), "-m", "pip", "install", f"uv=={UV_VERSION}"], check=True)
        env = dict(os.environ, UV_PYTHON_INSTALL_DIR=str(ROOT / "converter-python"), UV_CACHE_DIR=str(ROOT / "converter-cache"))
        subprocess.run([str(tools / "bin" / "uv"), "venv", "--python", "3.12", "--seed", str(env_dir)], env=env, check=True)
    interpreter = str(env_dir / "bin" / "python")
    subprocess.run([interpreter, "-m", "pip", "install", "torch==2.9.0", "torchaudio==2.9.0",
                    "--index-url", "https://download.pytorch.org/whl/cpu"], check=True)
    subprocess.run([interpreter, "-m", "pip", "install", "-r", str(native / "requirements.txt")], check=True)
    target = ROOT / "models" / "nemotron-en-f16.gguf"
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_suffix(".partial")
    subprocess.run([interpreter, str(native / "convert_model.py"),
                    "nvidia/nemotron-speech-streaming-en-0.6b", "--revision", ASR_REVISION,
                    "--outtype", "f16", "--outfile", str(temporary)], cwd=native, check=True)
    with temporary.open("rb") as stream:
        if stream.read(4) != b"GGUF":
            raise RuntimeError("Converter did not produce a GGUF model")
    temporary.replace(target)
    return target


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--asr-f16", action="store_true")
    parser.add_argument("--converter-python", default=os.environ.get("BEEFYER_CONVERSION_PYTHON"))
    args = parser.parse_args()
    print("VAD ready:", install_vad())
    if args.asr_f16:
        print("Higher-precision ASR ready:", install_f16(args.converter_python))


if __name__ == "__main__":
    main()
