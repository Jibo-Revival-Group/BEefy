#!/bin/sh
# Build NVIDIA's pinned CPU streaming runtime. Run as the BEefyER service user.
set -eu
beefyer_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
beefyer_native="$beefyer_dir/native"
beefyer_revision=e59dfb0b5c22655620f41391cc9a000d6f56b802
if [ ! -d "$beefyer_native/.git" ]; then
    git clone --no-checkout https://github.com/NVIDIA/NeMo-Speech.cpp.git "$beefyer_native"
fi
git -C "$beefyer_native" checkout --detach "$beefyer_revision"
git -C "$beefyer_native" submodule update --init llama.cpp third_party/cpp-httplib
cd "$beefyer_native"
bash scripts/configure.sh cpu-server -DNEMO_SPEECH_BUILD_TTS=OFF -DNEMO_SPEECH_BUILD_NMT=OFF -DNEMO_SPEECH_BUILD_DIAR=OFF
cmake --build --preset cpu-server --parallel "${BEEFYER_BUILD_JOBS:-4}"
cmake --install build/cpu-server --prefix "$beefyer_dir/runtime"
"$beefyer_dir/runtime/bin/nemo-speech" pull nemotron-en
printf '%s\n' "Native runtime installed: $beefyer_dir/runtime/bin/nemo-speech"
