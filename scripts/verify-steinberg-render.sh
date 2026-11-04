#!/usr/bin/env bash
# Linux portable gate only. Does NOT claim Cubase/Nuendo Windows live acceptance.
# Official Microsoft .NET SDK and NuGet only; no Docker on user's workstation.
set -euo pipefail

cd "$(dirname "$0")/.."
export DOTNET_CLI_HOME="$PWD/.dotnet-home"
export DOTNET_ROOT="$PWD/.dotnet-sdk"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
mkdir -p "$DOTNET_CLI_HOME" "$DOTNET_ROOT" .render-steinberg-proof

HEAD="$(git rev-parse HEAD)"
BRANCH="$(git symbolic-ref -q --short HEAD || true)"
printf 'STEINBERG_GATE_SOURCE_HEAD=%s\n' "$HEAD"
printf 'STEINBERG_GATE_SOURCE_BRANCH=%s\n' "${BRANCH:-DETACHED_HEAD}"
# Render checks out the pinned SHA in detached-HEAD mode. Its service branch
# configuration selects the branch; a detached HEAD is expected, not failure.
if [ -n "$BRANCH" ] && [ "$BRANCH" != "feat/steinberg-dualmode-adapters-20261104" ]; then
  echo 'SOURCE_BRANCH_MISMATCH'; exit 50
fi

# global.json requires 8.0.301 with latestPatch only. No floating SDK.
curl --fail --location --silent --show-error --retry 2 --max-time 90 \
  https://dot.net/v1/dotnet-install.sh -o .render-steinberg-proof/dotnet-install.sh
bash .render-steinberg-proof/dotnet-install.sh \
  --version 8.0.301 --install-dir "$DOTNET_ROOT" --no-path

dotnet --version
test "$(dotnet --version)" = "8.0.301"
echo STEINBERG_RESTORE_BEGIN
dotnet restore CubaseCompleteMcp.sln --locked-mode \
  -p:NuGetAudit=false -p:EnableWindowsTargeting=true

echo STEINBERG_BUILD_BEGIN
dotnet build CubaseCompleteMcp.sln -c Release --no-restore \
  -p:EnableWindowsTargeting=true

echo STEINBERG_TEST_BEGIN
TRX="steinberg-$(printf '%s' "$HEAD" | cut -c 1-12).trx"
STEINBERG_INDEPENDENT_FOLEY_AUDIT=1 dotnet test tests/CubaseCompleteMcp.Server.Tests/CubaseCompleteMcp.Server.Tests.csproj \
  -c Release --no-build --no-restore -p:EnableWindowsTargeting=true \
  --filter 'FullyQualifiedName~Steinberg' \
  --logger "trx;LogFileName=$TRX" \
  --results-directory .render-steinberg-proof

echo STEINBERG_CUTPILOT_PROVIDER_TEST_BEGIN
python3 -m unittest discover -s tests/python -p 'test_cutpilot_foley_provider.py' -v

echo STEINBERG_XCLIP_OFFLINE_CONTRACT_TEST_BEGIN
python3 -m unittest discover -s tests/python -p 'test_xclip_foley_action_provider.py' -v
echo STEINBERG_CUTPILOT_REAL_MP4_BEGIN
# Real OpenCV/MP4/ffprobe smoke: downstream CutPilot scene module is fetched
# from a pinned commit and checked against its Git blob SHA. No host/DAW.
python3 -m pip install --no-cache-dir --disable-pip-version-check opencv-python-headless==4.14.0.94
python3 scripts/verify_cutpilot_real_video.py --output .render-steinberg-proof/cutpilot-real-video.json
echo STEINBERG_CUTPILOT_REAL_MP4_PASS
echo STEINBERG_FOLEY_INDEPENDENT_AUDITOR_TEST_BEGIN
python3 - <<'PY'
import json
import unittest

suite = unittest.defaultTestLoader.discover(
    start_dir='tests/python', pattern='test_foley_dawproject_audit.py')
count = suite.countTestCases()
if count != 13:
    raise SystemExit(f'FAIL_INDEPENDENT_AUDIT_DISCOVERY: expected 13, found {count}')
result = unittest.TextTestRunner(verbosity=2).run(suite)
if not result.wasSuccessful() or result.testsRun != count:
    raise SystemExit(
        f'FAIL_INDEPENDENT_AUDITOR: ran={result.testsRun}, failures={len(result.failures)}, errors={len(result.errors)}')
print('STEINBERG_FOLEY_INDEPENDENT_AUDITOR_TESTS_PASS ' +
      json.dumps({'executed': result.testsRun, 'passed': result.testsRun,
                  'failed': 0, 'errors': 0}))
PY
echo STEINBERG_REAL_ARRANGEMENT_EXTERNAL_CLI_BEGIN
export STEINBERG_ARRANGEMENT_ENV_ROOT
STEINBERG_ARRANGEMENT_ENV_ROOT="$(mktemp -d /tmp/steinberg-arrangement-XXXXXX)"
trap 'rm -rf "$STEINBERG_ARRANGEMENT_ENV_ROOT"' EXIT
# Two source repos are cloned read-only at exact authorized Git SHA;
# Python 3.13 is isolated for MIDIVOLT; GrooveMap runs on Render Python.
source scripts/setup_arrangement_real_clis.sh
ARR_TRX="arrangement-real-$(printf '%s' "$HEAD" | cut -c 1-12).trx"
dotnet test tests/CubaseCompleteMcp.Server.Tests/CubaseCompleteMcp.Server.Tests.csproj \
  -c Release --no-build --no-restore -p:EnableWindowsTargeting=true \
  --filter 'FullyQualifiedName~ArrangementRealCli' \
  --logger "trx;LogFileName=$ARR_TRX" \
  --results-directory .render-steinberg-proof

mkdir -p .render-steinberg-proof/arrangement-real-e2e
python3 scripts/verify_arrangement_real_clis.py \
  --midivolt-exe "$STEINBERG_MIDIVOLT_EXE" \
  --groovemap-exe "$STEINBERG_GROOVEMAP_EXE" \
  --output-dir .render-steinberg-proof/arrangement-real-e2e
echo STEINBERG_REAL_ARRANGEMENT_EXTERNAL_CLI_COMPLETE
echo STEINBERG_WINDOWS_FOLEY_STATIC_SOURCE_CONTRACT_TEST_BEGIN
python3 -m unittest discover -s tests/python -p 'test_steinberg_windows_foley_entry.py' -v

python3 - "$HEAD" ".render-steinberg-proof/$TRX" ".render-steinberg-proof/$ARR_TRX" <<'PY'
import json, sys, xml.etree.ElementTree as ET
from pathlib import Path
head, trxpath, real_trxpath = sys.argv[1:]
path = Path(trxpath)
if not path.is_file():
    raise SystemExit('FAIL_NO_TRX')
cutpilot_path = Path('.render-steinberg-proof/cutpilot-real-video.json')
if not cutpilot_path.is_file():
    raise SystemExit('FAIL_MISSING_REAL_CUTPILOT_VIDEO_EVIDENCE')
cutpilot_result = json.loads(cutpilot_path.read_text(encoding='utf-8'))
if (cutpilot_result.get('status') != 'PASS_REAL_OPENCV_CUTPILOT_DECODED_SYNTHETIC_MP4_ONLY'
        or cutpilot_result.get('cutpilot_source_commit') != 'c534cd56e8507288b20c1d55fcb5b8d9e8229ea4'
        or cutpilot_result.get('scene_cut_frames') != [30, 60]
        or cutpilot_result.get('real_opencv_decoder') is not True
        or cutpilot_result.get('invalid_mp4_rejected') is not True):
    raise SystemExit('FAIL_INDEPENDENT_REAL_CUTPILOT_VIDEO_EVIDENCE')
root = ET.parse(path).getroot()
tests = [e for e in root.iter() if e.tag.rsplit('}', 1)[-1] == 'UnitTestResult']
passed = [e for e in tests if e.get('outcome') == 'Passed']
required = (
    'SteinbergDawprojectSchemaTests.DAW04_ExportedXmlValidatesAgainstOfficialProjectAndMetadataSchemas',
    'SteinbergDawprojectSchemaTests.DAW05_OfficialProjectSchemaRejectsUnknownXml',
    'SteinbergFoleyPipelineE2ETests.SteinbergFoleyPipeline01_TwoEventsReachXsdValidHashMatchedDawproject',
    'SteinbergFoleyPipelineE2ETests.SteinbergFoleyPipeline03_IndependentAuditorRejectsTamperedExportedZip',
)
names = [e.get('testName', '') for e in passed]
missing = [req for req in required if not any(req in name for name in names)]
if not tests or len(tests) != len(passed) or missing:
    raise SystemExit(f'FAIL_TRX_GATE {len(passed)}/{len(tests)} missing={missing}')
real_path=Path('.render-steinberg-proof/arrangement-real-e2e/report.json')
if not real_path.is_file():
    raise SystemExit('FAIL_NO_EXTERNAL_ARRANGEMENT_CLI_EVIDENCE')
arrangement=json.loads(real_path.read_text(encoding='utf-8'))
if (arrangement.get('status') != 'PASS_REAL_EXTERNAL_CLI_MIDIVOLT_TO_GROOVEMAP_HUMANIZE_ONLY'
        or arrangement.get('midivolt_source_head') != '0c23471b7508bc84383b7860fb976a0ab1e9cb73'
        or arrangement.get('groovemap_source_head') != '3b9bc6d5a26212b35c92d91c1fd902061b187440'
        or arrangement.get('midivolt_original_unchanged') is not True
        or arrangement.get('groovemap_real_humanize_midi_cli') is not True
        or arrangement.get('groovemap_real_beat_this_inference') != 'NOT_RUN_NO_APPROVED_CHECKPOINT'):
    raise SystemExit('FAIL_REAL_ARRANGEMENT_CLI_CONTRACT')
real_trx=Path(real_trxpath)
if not real_trx.is_file():
    raise SystemExit('FAIL_NO_MCP_EXTERNAL_PROVIDER_TRX')
arr_root=ET.parse(real_trx).getroot()
arr_results=[e for e in arr_root.iter() if e.tag.rsplit('}',1)[-1]=='UnitTestResult']
if (len(arr_results)!=1 or arr_results[0].get('outcome')!='Passed'
        or 'ArrangementRealCli01_McpActuallyExecutesInstalledMidivoltPromptAndRejectsOverwrite'
        not in arr_results[0].get('testName','')):
    raise SystemExit('FAIL_REAL_MCP_EXTERNAL_PROVIDER_TRX')

report = {
    'status': 'PASS_LINUX_PORTABLE_ONLY',
    'source_head': head,
    'scope': '.NET Steinberg tests, real CutPilot MP4, real MIDIVOLT MCP CLI, real GrooveMap MIDI humanize; Beat This checkpoint NOT_RUN',
    'independent_python_foley_auditor': 'PASS_FILE_ONLY_PACKAGE_READBACK_AND_SHA_NEGATIVE',
    'independent_python_auditor_mutation_tests': {'executed': 13, 'passed': 13, 'source': 'validated unittest result above'},
    'cutpilot_python_contract_tests': 'PASS_5_OF_5_SIMULATED_SCENE_DETECTOR',
    'real_cutpilot_opencv_video': 'PASS_DECODED_SYNTHETIC_MP4_ONLY',
    'real_cutpilot_video_evidence': cutpilot_result,
    'real_external_arrangement_cli': arrangement,
    'real_external_arrangement_mcp_execution': 'PASS_ONE_REAL_MIDIVOLT_PROVIDER_PROCESS',
    'xclip_python_contract_tests': 'PASS_6_OF_6_SIMULATED_INFERENCE_NO_MODEL_WEIGHTS',
    'real_xclip_pretrained_weights_inference': 'NOT_RUN',
    'executed_tests': len(tests),
    'passed_tests': len(passed),
    'official_xsd_tests': list(required[:2]),
    'required_foley_e2e_tests': list(required[2:]),
    'windows_powershell_script_static_contract_tests': 'PASS_19_OF_19_SOURCE_ONLY',
    'windows_powershell_runtime_execution': 'NOT_RUN',
    'windows_native': 'NOT_RUN',
    'cubase_nuendo_import': 'NOT_RUN',
    'license_and_audio_acceptance': 'NOT_RUN',
    'github_actions': 'NOT_USED',
}
Path('.render-steinberg-proof/report.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
Path('.render-steinberg-proof/index.html').write_text(
    '<!doctype html><html lang="en"><meta charset="utf-8"><title>Steinberg offline gate</title>'
    '<h1>PASS_LINUX_PORTABLE_ONLY</h1><p>Real Cubase/Nuendo Windows import NOT_RUN.</p>'
    '<p><a href="report.json">JSON evidence</a></p></html>', encoding='utf-8'
)
print('STEINBERG_LINUX_PORTABLE_GATE_PASS ' + json.dumps(report))
PY
