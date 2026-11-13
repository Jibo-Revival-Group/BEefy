#!/usr/bin/env python3
"""Install what BEefy needs for Jibo to speak Spanish and Romanian in his own voice (the speech language in BEacon).

Creates App_Data/speech-translation with its own Python environment (CTranslate2 + SentencePiece) and the pinned
translation models. Also needs eSpeak-NG on the machine: sudo apt-get install -y espeak-ng
Run it again any time; finished steps are skipped. Restart BEefy afterwards.
"""
import argparse
import hashlib
import shutil
import subprocess
import sys
import tarfile
import urllib.request
from pathlib import Path

MODELS_URL = "https://github.com/Jibo-Revival-Group/BEefy/releases/download/speech-translation-models-1/"
MODELS = {  # OPUS-MT (Helsinki-NLP, Apache-2.0) converted to CTranslate2 int8
    "en-es": ("opus-mt-en-es-ct2-int8.tar.gz", "6a77485551e2715d4178e51941b10926afc991c6c8a6e455e31786b8a67b818c"),
    "en-ro": ("opus-mt-en-ro-ct2-int8.tar.gz", "557a70270613188c321258813f6cc514f6b06089fc68f14fb95b2911ee0ef6c0"),
}
PACKAGES = ["ctranslate2==4.9.0", "sentencepiece==0.2.3"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, help="default: App_Data/speech-translation in this checkout")
    parser.add_argument("--python", default=sys.executable, help="Python 3.10-3.13 with venv support")
    args = parser.parse_args()
    directory = args.directory or Path(__file__).resolve().parents[2] / "App_Data" / "speech-translation"
    directory.mkdir(parents=True, exist_ok=True)

    if not shutil.which("espeak-ng"):
        sys.exit("eSpeak-NG is missing. Install it first: sudo apt-get install -y espeak-ng")

    python = directory / "venv" / "bin" / "python"
    if not python.exists():
        subprocess.run([args.python, "-m", "venv", str(directory / "venv")], check=True)
    subprocess.run([str(python), "-m", "pip", "install", "--quiet", "--disable-pip-version-check", *PACKAGES], check=True)

    for name, (archive, sha256) in MODELS.items():
        if (directory / name / "model.bin").exists():
            continue
        print(f"Downloading {archive}", flush=True)
        target = directory / archive
        with urllib.request.urlopen(MODELS_URL + archive, timeout=300) as source, target.open("wb") as output:
            shutil.copyfileobj(source, output)
        if hashlib.sha256(target.read_bytes()).hexdigest() != sha256:
            target.unlink()
            sys.exit(f"Checksum mismatch: {archive}")
        with tarfile.open(target) as tar:
            tar.extractall(directory, filter="data")
        target.unlink()

    subprocess.run([str(python), "-c", "import ctranslate2, sentencepiece"], check=True)
    print(f"Speech translation is installed in {directory}. Restart BEefy to use it.")


if __name__ == "__main__":
    main()
