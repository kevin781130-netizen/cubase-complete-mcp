#!/usr/bin/env bash
# True, isolated X-CLIP pretrained inference gate. No live DAW and no fake model.
# Run only on the dedicated, manually deployed Render static-site build.
set -Eeuo pipefail
cd "$(dirname "$0")/.."
export PYTHONUNBUFFERED=1
export PIP_DISABLE_PIP_VERSION_CHECK=1
export HF_HUB_DISABLE_TELEMETRY=1
export HF_HUB_DISABLE_XET=1
export TOKENIZERS_PARALLELISM=false
export OMP_NUM_THREADS=2
export MKL_NUM_THREADS=2
export OPENBLAS_NUM_THREADS=2
export HF_HOME="$PWD/.render-xclip-cache"
OUT="$PWD/.render-xclip-real-proof"
mkdir -p "$OUT" "$HF_HOME"
echo "XCLIP_REAL_GATE_SOURCE_SHA=$(git rev-parse HEAD)"
echo "XCLIP_REAL_GATE_PYTHON=$(python3 --version 2>&1)"
echo "XCLIP_REAL_GATE_NO_ACTIONS=0"

# A dedicated Python 3.11 build environment is needed for the pinned CPU wheels.
python3 - <<'PY'
import sys
if sys.version_info[:2] != (3, 11):
    raise SystemExit("ENVIRONMENT_BLOCKED_PYTHON_3_11_REQUIRED")
PY
python3 -m venv .render-xclip-venv
source .render-xclip-venv/bin/activate
python -m pip install --disable-pip-version-check --no-cache-dir --timeout 45 \
  'torch==2.5.1' 'torchvision==0.20.1' \
  --index-url https://download.pytorch.org/whl/cpu
python -m pip install --disable-pip-version-check --no-cache-dir --timeout 45 \
  'transformers==4.49.0' 'huggingface_hub==0.30.2' \
  'opencv-python-headless==4.10.0.84' 'numpy==1.26.4' \
  'safetensors==0.5.3' 'Pillow==11.1.0' 'sentencepiece==0.2.0' 'protobuf==4.25.5'
python - <<'PY'
import torch, transformers, cv2, huggingface_hub
print('XCLIP_VERSIONS',torch.__version__,transformers.__version__,cv2.__version__,huggingface_hub.__version__)
PY

if command -v ffmpeg >/dev/null && command -v ffprobe >/dev/null; then
  export FFMPEG="$(command -v ffmpeg)" FFPROBE="$(command -v ffprobe)"
else
  # Officially published standalone FFmpeg/FFprobe NPM binaries, no apt/Docker.
  npm --prefix .render-xclip-tools install --no-save --no-audit --no-fund \
    '@ffmpeg-installer/ffmpeg@1.1.0' '@ffprobe-installer/ffprobe@2.1.2'
  export FFMPEG="$(node -p "require('./.render-xclip-tools/node_modules/@ffmpeg-installer/ffmpeg').path")"
  export FFPROBE="$(node -p "require('./.render-xclip-tools/node_modules/@ffprobe-installer/ffprobe').path")"
fi
"$FFMPEG" -version | head -n 1
"$FFPROBE" -version | head -n 1

# A real people-walking footage sample from the public OpenCV repository.
curl --fail --location --silent --show-error --retry 2 --max-time 100 \
  'https://raw.githubusercontent.com/opencv/opencv/4.x/samples/data/vtest.avi' \
  -o "$OUT/opencv-vtest-original.avi"
test "$(wc -c < "$OUT/opencv-vtest-original.avi")" -gt 100000
"$FFMPEG" -nostdin -hide_banner -loglevel error -y \
  -i "$OUT/opencv-vtest-original.avi" -t 2 \
  -vf 'fps=12,scale=320:-2' -an -c:v mpeg4 -q:v 5 \
  "$OUT/pedestrians-two-seconds.mp4"
"$FFPROBE" -v error -select_streams v:0 \
  -show_entries stream=avg_frame_rate,nb_frames,width,height \
  -of json "$OUT/pedestrians-two-seconds.mp4"

# Pin the official 2024 safetensors release; no untrusted remote model code.
python - <<'PY'
import hashlib, json
from huggingface_hub import snapshot_download
from pathlib import Path
repo = "microsoft/xclip-base-patch32"
revision = "a2e27a78a2b5d802e894b8a1ef14f3a8ce490963"
snapshot = snapshot_download(
    repo_id=repo, revision=revision,
    allow_patterns=[
        "config.json", "preprocessor_config.json", "model.safetensors",
        "tokenizer.json", "tokenizer_config.json", "special_tokens_map.json",
        "vocab.json", "merges.txt"
    ],
)
model = Path(snapshot)
weight = model / "model.safetensors"
actual_hash = hashlib.file_digest(weight.open("rb"), "sha256").hexdigest()
expected_hash = "abf286e8cdd0612761c3e42d3a55eca998382dfa67a04a0f3fdcdfa4f150cdbb"
if actual_hash != expected_hash:
    raise SystemExit("MODEL_HASH_MISMATCH: " + actual_hash)
Path(".render-xclip-real-proof/model-path.txt").write_text(str(model),encoding="utf-8")
print("XCLIP_AUTHENTIC_SAFETENSORS_SHA256=" + actual_hash)
print("XCLIP_AUTHENTIC_WEIGHTS_BYTES=" + str(weight.stat().st_size))
PY
MODEL_DIR="$(cat "$OUT/model-path.txt")"
export HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1
echo 'XCLIP_REAL_CPU_INFERENCE_BEGIN'
python scripts/xclip_foley_action_provider.py \
  --video "$OUT/pedestrians-two-seconds.mp4" \
  --model-dir "$MODEL_DIR" --ffprobe "$FFPROBE" \
  --min-score 0.30 --min-margin 0.05 > "$OUT/raw-inference.json"

python - <<'PY'
import hashlib, json, subprocess, sys
from pathlib import Path
p=Path('.render-xclip-real-proof')
r=json.loads((p/'raw-inference.json').read_text())
assert r['model_executed'] is True
assert r['provider']=='microsoft.xclip.zero_shot_local'
assert r['automatic_action_recognition'] is True
assert r['human_review_required'] is True
assert r['sample_audio_identified'] is False
assert r['video_frame_count'] >= 8
assert r['windows_evaluated'] >= 1
assert r['sampled_frames_per_window']==8
assert isinstance(r['cues'],list)
assert all(c['action_class'] in {'walking','running','opening_door','closing_door','object_impact','clapping','driving','handling_cloth'} for c in r['cues'])
revision='a2e27a78a2b5d802e894b8a1ef14f3a8ce490963'
report={
 'status':'PASS_REAL_XCLIP_CPU_INFERENCE_NOT_ACCURACY_VALIDATED',
 'source_sha':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),
 'model_repo':'microsoft/xclip-base-patch32',
 'model_revision':revision,
 'model_safetensors_sha256':'abf286e8cdd0612761c3e42d3a55eca998382dfa67a04a0f3fdcdfa4f150cdbb',
 'sample_source':'https://github.com/opencv/opencv/blob/4.x/samples/data/vtest.avi',
 'sample_transformation':'First 2s reencoded at CFR 12fps, max 320px width',
 'sample_sha256':hashlib.sha256((p/'pedestrians-two-seconds.mp4').read_bytes()).hexdigest(),
 'action_candidates_not_confirmed':r['cues'],
 'video_fps_numerator':r['fps_numerator'],
 'video_fps_denominator':r['fps_denominator'],
 'video_frames':r['video_frame_count'],
 'windows_evaluated':r['windows_evaluated'],
 'frames_per_window':r['sampled_frames_per_window'],
 'accuracy_validation':'NOT_RUN',
 'ground_truth_action_annotations':'NOT_AVAILABLE',
 'automatic_sfx_matching':'NOT_RUN',
 'cubase_nuendo_windows':'NOT_RUN',
 'note':'Real pretrained weights and real OpenCV pedestrian video; outcomes are uncalibrated similarity and window-center approximations only.',
}
(p/'report.json').write_text(json.dumps(report,indent=2,ensure_ascii=False)+'\n')
(p/'index.html').write_text(
    '<!doctype html><html><meta charset="utf-8"><title>True X-CLIP inference</title>'
    '<h1>PASS_REAL_XCLIP_CPU_INFERENCE_NOT_ACCURACY_VALIDATED</h1>'
    '<p>True Microsoft safetensors on real OpenCV pedestrian video. Action accuracy NOT validated.</p>'
    '<p><a href="report.json">JSON evidence</a></p></html>',encoding='utf-8')
print('XCLIP_AUTHENTIC_CPU_INFERENCE_PASS '+json.dumps(report))
PY
echo XCLIP_WEAK_LABEL_DIAGNOSTIC_CONTRACT_TEST_BEGIN
python -m unittest discover -s tests/python -p 'test_xclip_foley_benchmark.py' -v

echo XCLIP_THREE_COMMONS_VIDEO_INFERENCE_BEGIN
python scripts/benchmark_xclip_commons.py "$MODEL_DIR" "$FFMPEG" "$FFPROBE"

# Candidate survey is opt-in and was previously reviewed. Do not re-download rejected media in each inference run.
python - <<'PY'
import json
from pathlib import Path
p = Path('.render-xclip-real-proof')
report = json.loads((p/'report.json').read_text(encoding='utf8'))
diagnostics = json.loads((p/'diagnostic-report.json').read_text(encoding='utf8'))
if len(diagnostics['samples']) != 4 or diagnostics['source_head'] != report['source_sha']:
    raise SystemExit('INVALID_THREE_VIDEO_DIAGNOSTICS')
audited = diagnostics.get('human_frame_review_summary') or {}
if (audited.get('confirmed_visible_positive_clip_count') != 2 or
        audited.get('excluded_unconfirmed_clip_count') != 2 or
        audited.get('dataset_recall') != 'NOT_ESTIMATED' or
        audited.get('production_threshold_changed') is not False):
    raise SystemExit('INVALID_CHECKSUM_LOCKED_HUMAN_FRAME_REVIEW')
report['human_frame_review_summary'] = audited
report['three_clip_weak_label_diagnostics'] = 'PASS_REAL_WEIGHTS_WITH_HASHED_STILL_REVIEW_NOT_GROUND_TRUTH'
report['diagnostic_report'] = 'diagnostic-report.json'
report['weak_label_summary'] = diagnostics['summary']
(p/'report.json').write_text(json.dumps(report, indent=2, ensure_ascii=False)+'\n',encoding='utf8')
html = (p/'index.html').read_text(encoding='utf8')
html = html.replace('</html>', '<p><a href="diagnostic-report.json">Three-video raw logits & possible misses</a></p></html>')
(p/'index.html').write_text(html, encoding='utf8')
print('XCLIP_REAL_LABELED_DIAGNOSTIC_GATE_PASS ' + json.dumps(diagnostics['summary']))
PY

echo SIGLIP_PAIRED_BASELINE_CONTRACT_TEST_BEGIN
python -m unittest discover -s tests/python -p 'test_siglip_xclip_baseline.py' -v

# Independent image-only Apache-2.0 baseline. Neither X-CLIP nor the live MCP
# production thresholds may be changed in response to this tiny benchmark.
unset HF_HUB_OFFLINE TRANSFORMERS_OFFLINE
python - <<'PY'
from pathlib import Path
import sys
sys.path.insert(0, str(Path("scripts").resolve()))
from huggingface_hub import snapshot_download
from compare_siglip_xclip import MODEL_REPO, MODEL_REVISION, verify_model
# Keep SigLIP weights outside the public static site; no pickle weights.
directory = Path(snapshot_download(
    repo_id=MODEL_REPO, revision=MODEL_REVISION,
    allow_patterns=[
        "config.json", "model.safetensors", "preprocessor_config.json",
        "spiece.model", "tokenizer.json", "tokenizer_config.json",
        "special_tokens_map.json",
    ]
))
checked_sha = verify_model(directory)
Path('.render-xclip-real-proof/siglip-model-dir.txt').write_text(str(directory), encoding='utf8')
print('SIGLIP_OFFICIAL_SHA256_PASS=' + checked_sha)
PY
export HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1
echo SIGLIP_PAIRED_REAL_CPU_INFERENCE_BEGIN
python scripts/compare_siglip_xclip.py "$(cat "$OUT/siglip-model-dir.txt")" "$FFMPEG" "$FFPROBE"

python - <<'PY'
from pathlib import Path
import json
root = Path('.render-xclip-real-proof')
xclip = json.loads((root/'report.json').read_text(encoding='utf8'))
paired = json.loads((root/'siglip-comparison.json').read_text(encoding='utf8'))
if xclip['source_sha'] != paired['source_sha'] or paired['reviewed_positive_summary']['reviewed_visible_positive_excerpts'] != 2:
    raise SystemExit('FAIL_SIGLIP_XCLIP_COMPARISON_PROVENANCE')
if not paired.get('real_pretrained_cpu_inference') or not paired.get('same_source_and_decoded_clips_as_xclip'):
    raise SystemExit('FAIL_REAL_WEIGHT_FRAME_MATCHING')
xclip['independent_siglip_baseline'] = {
    'status':'PASS_REAL_SIGLIP_IMAGE_BASELINE_NO_VIDEO_ACCURACY_CLAIM',
    'model_repo':paired['model_repo'],
    'model_revision':paired['model_revision'],
    'model_safetensors_sha256':paired['model_safetensors_sha256'],
    'report':'siglip-comparison.json',
    'reviewed_positive_summary':paired['reviewed_positive_summary']
}
(root/'report.json').write_text(json.dumps(xclip,indent=2,ensure_ascii=False)+'\n',encoding='utf8')
html = (root/'index.html').read_text(encoding='utf8')
html = html.replace('</html>', '<p><a href="siglip-comparison.json">SigLIP vs X-CLIP model comparison</a></p></html>')
(root/'index.html').write_text(html,encoding='utf8')
print('SIGLIP_REAL_COMPARISON_PASS='+json.dumps(paired['reviewed_positive_summary']))
PY

# A static-site build can report success while its JSON is malformed: fail closed.
python3 -m json.tool "$OUT/report.json" >/dev/null
python3 -m json.tool "$OUT/diagnostic-report.json" >/dev/null
python3 -m json.tool "$OUT/siglip-comparison.json" >/dev/null
echo STEINBERG_ALL_PUBLISHED_EVIDENCE_JSON_VALID

# Never publish original/modified media or model weights as public Render assets.
rm -f "$OUT/opencv-vtest-original.avi" "$OUT/pedestrians-two-seconds.mp4" "$OUT/model-path.txt" "$OUT/siglip-model-dir.txt"
rmdir "$OUT/_benchmark_media_not_published" 2>/dev/null || true
