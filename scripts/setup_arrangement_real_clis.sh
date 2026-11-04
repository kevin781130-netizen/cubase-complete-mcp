#!/usr/bin/env bash
# SOURCE this from scripts/verify-steinberg-render.sh after .NET restore/build.
# Isolated, disposable cloud-only provider install. NO GitHub Actions / Docker.
set -euo pipefail
: "$STEINBERG_ARRANGEMENT_ENV_ROOT"
ROOT="$STEINBERG_ARRANGEMENT_ENV_ROOT"
GROOVE_SHA=3b9bc6d5a26212b35c92d91c1fd902061b187440
MIDI_SHA=0c23471b7508bc84383b7860fb976a0ab1e9cb73

echo STEINBERG_ARRANGEMENT_PINNED_CHECKOUT_BEGIN
git clone --quiet --depth 1 --single-branch \
  --branch fix/groovemap-v3-preflight-output-safety-20261102 \
  https://github.com/kevin781130-netizen/GrooveMap.git "$ROOT/GrooveMap"
git clone --quiet --depth 1 --single-branch \
  --branch work/midivolt-ready-real-machine-20261103 \
  https://github.com/kevin781130-netizen/MIDIVOLT.git "$ROOT/MIDIVOLT"

test "$(git -C "$ROOT/GrooveMap" rev-parse HEAD)" = "$GROOVE_SHA" \
  || { echo GROOVEMAP_SOURCE_HEAD_MISMATCH; return 51; }
test "$(git -C "$ROOT/MIDIVOLT" rev-parse HEAD)" = "$MIDI_SHA" \
  || { echo MIDIVOLT_SOURCE_HEAD_MISMATCH; return 52; }
echo "STEINBERG_ARRANGEMENT_UPSTREAM_HEADS_PINNED groove=$GROOVE_SHA midi=$MIDI_SHA"

# The active Render Python version is 3.14, supported by GrooveMap's core
# but NOT MIDIVOLT (Python >=3.11,<3.14). Use separate Python 3.13 venv.
echo STEINBERG_ARRANGEMENT_REAL_DEPENDENCIES_BEGIN
python3 -m pip install -q --disable-pip-version-check --no-cache-dir \
  -e "$ROOT/GrooveMap/groovemap_next" uv==0.9.17

python3 -m uv venv --python 3.13 --seed "$ROOT/python313"
python3 -m uv pip install --python "$ROOT/python313/bin/python" \
  -e "$ROOT/MIDIVOLT"
"$ROOT/python313/bin/python" -c \
  'import sys,music21,pretty_midi; assert sys.version_info[:2] == (3,13); print("MIDIVOLT_REAL_DEPENDENCIES_OK",music21.__version__)'
python3 -c \
  'import groovemap_next,mido; print("GROOVEMAP_REAL_DEPENDENCIES_OK",mido.__version__)'

# Linux executable wrappers emulate the exact Windows pip entrypoint basename
# expected by the C# adapter, but run the real Python distribution/CLI.
# They are test-only and never shipped with Windows releases.
cat > "$ROOT/midivolt-prompt.exe" <<EOF
#!/bin/sh
exec "$ROOT/python313/bin/midivolt-prompt" "\$@"
EOF
cat > "$ROOT/groovemap-next.exe" <<EOF
#!/bin/sh
exec "$(command -v groovemap-next)" "\$@"
EOF
chmod 700 "$ROOT/midivolt-prompt.exe" "$ROOT/groovemap-next.exe"
export STEINBERG_MIDIVOLT_EXE="$ROOT/midivolt-prompt.exe"
export STEINBERG_GROOVEMAP_EXE="$ROOT/groovemap-next.exe"
export STEINBERG_REAL_ARRANGEMENT_E2E=1
echo STEINBERG_ARRANGEMENT_REAL_PROVIDER_SETUP_PASS
