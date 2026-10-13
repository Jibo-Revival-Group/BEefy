#!/bin/sh
set -eu
beefyer_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
python3 -m venv "$beefyer_dir/.venv"
"$beefyer_dir/.venv/bin/python" -m pip install -r "$beefyer_dir/requirements.txt"
"$beefyer_dir/.venv/bin/python" "$beefyer_dir/prepare-models.py"
printf '%s\n' 'Python worker ready. See README.md to build the pinned native runtime and configure the service.'
