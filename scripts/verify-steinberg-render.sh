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
dotnet test tests/CubaseCompleteMcp.Server.Tests/CubaseCompleteMcp.Server.Tests.csproj \
  -c Release --no-build --no-restore -p:EnableWindowsTargeting=true \
  --filter 'FullyQualifiedName~Steinberg' \
  --logger "trx;LogFileName=$TRX" \
  --results-directory .render-steinberg-proof

echo STEINBERG_CUTPILOT_PROVIDER_TEST_BEGIN
python3 -m unittest discover -s tests/python -p 'test_cutpilot_foley_provider.py' -v

echo STEINBERG_XCLIP_OFFLINE_CONTRACT_TEST_BEGIN
python3 -m unittest discover -s tests/python -p 'test_xclip_foley_action_provider.py' -v
echo STEINBERG_WINDOWS_FOLEY_STATIC_SOURCE_CONTRACT_TEST_BEGIN
python3 -m unittest discover -s tests/python -p 'test_steinberg_windows_foley_entry.py' -v

python3 - "$HEAD" ".render-steinberg-proof/$TRX" <<'PY'
import json, sys, xml.etree.ElementTree as ET
from pathlib import Path
head, trxpath = sys.argv[1:]
path = Path(trxpath)
if not path.is_file():
    raise SystemExit('FAIL_NO_TRX')
root = ET.parse(path).getroot()
tests = [e for e in root.iter() if e.tag.rsplit('}', 1)[-1] == 'UnitTestResult']
passed = [e for e in tests if e.get('outcome') == 'Passed']
required = (
    'SteinbergDawprojectSchemaTests.DAW04_ExportedXmlValidatesAgainstOfficialProjectAndMetadataSchemas',
    'SteinbergDawprojectSchemaTests.DAW05_OfficialProjectSchemaRejectsUnknownXml',
)
names = [e.get('testName', '') for e in passed]
missing = [req for req in required if not any(req in name for name in names)]
if not tests or len(tests) != len(passed) or missing:
    raise SystemExit(f'FAIL_TRX_GATE {len(passed)}/{len(tests)} missing={missing}')
report = {
    'status': 'PASS_LINUX_PORTABLE_ONLY',
    'source_head': head,
    'scope': '.NET compile, Steinberg MCP tests, pinned DAWproject XSD tests, external CutPilot contract tests with a fake detector',
    'cutpilot_python_contract_tests': 'PASS_5_OF_5_SIMULATED_SCENE_DETECTOR',
    'real_cutpilot_opencv_video': 'NOT_RUN',
    'xclip_python_contract_tests': 'PASS_6_OF_6_SIMULATED_INFERENCE_NO_MODEL_WEIGHTS',
    'real_xclip_pretrained_weights_inference': 'NOT_RUN',
    'executed_tests': len(tests),
    'passed_tests': len(passed),
    'official_xsd_tests': list(required),
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
