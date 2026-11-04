# Steinberg dual-mode MCP — pre-device closeout (2026-11-04)

## Status

**CLOUD_PORTABLE_PASS / HOST_ACCEPTANCE_NOT_RUN.** This report is an evidence boundary, **not** a production or Windows/Cubase/Nuendo release approval.

- Repository: `kevin781130-netizen/cubase-complete-mcp`
- Candidate: `feat/steinberg-dualmode-adapters-20261104` (do not merge to `main` without host evidence)
- CI-equivalent (without GitHub Actions): manually deployed Render Linux portable gate.
- Authoritative tested source SHA: read `source_head` from the currently live [portable report](https://steinberg-dualmode-dotnet-portable.onrender.com/report.json) and compare it with the candidate's Git HEAD. A deployment at an older SHA does not validate later commits.
- Separate X-CLIP/SigLIP evidence: [real-model report](https://steinberg-xclip-real-weights-20261104.onrender.com/report.json). It used source SHA `5458d2dbf0bedcb561ac6dd94dd07a596b98b09e`; later changes through the prior portable gate `0559b40be39a9785599df9c1888fab5fe2ef9326` concerned sample/DAWproject export, auditor, tests and gate, **not** the X-CLIP/SigLIP model scripts. This is not a fresh full-model rerun at the later HEAD.

## Verified offline only

| Capability | Evidence and boundary |
|---|---|
| Core solution compiles | Render Linux builds the full `.sln` with pinned .NET 8.0.301 and locked NuGet packages; no actual Windows driver launched. |
| Steinberg MCP/DAWproject tests | `dotnet test` with filter `FullyQualifiedName~Steinberg`; historical `0559b40` gate: **58/58** passed, with official `Project.xsd` and `MetaData.xsd` tests. The filter is **not** the entire repository test suite. |
| Foley candidate → scripted approvals → human-timing fixture → portable export | Synthetic test inputs only; sample/video identities, 29.97-drop-frame offsets, tracks and ZIP round-trip verified offline. Test approvals are deliberately simulated, **not** proof a person listened or verified legal rights. |
| CutPilot actual compressed video execution | Render `scripts/verify_cutpilot_real_video.py` requires real `opencv-python-headless==4.14.0.94`, `ffprobe`, actual MP4 encoder and decoder, two independently expected scene cuts (frames 30/60 at 24fps), and a fail-closed invalid MP4 check. **Synthetic footage only**; no real actor/action inference. Evidence: `report.json.real_cutpilot_video_evidence` and `cutpilot-real-video.json`. |
| Independent DAWproject ZIP audit | Standalone `scripts/audit_foley_dawproject.py` separately parses the actual `.dawproject` exported by .NET, hashes embedded WAVs, checks tracks and frame-to-seconds placement, and rejects mutated audio. Thirteen separate mutation tests. |
| Negative-case gate integrity | The Render gate must **count and pass exactly 13 independently executed Python auditor cases**, not merely echo a hardcoded PASS marker; the emitted JSON separates XSD tests from required Foley E2E cases. |
| CutPilot provider and X-CLIP lightweight contracts | Mocked provider contracts still run, **plus** the portable gate now runs real FFprobe and the authentic CutPilot OpenCV detector against a 90-frame encoded MP4 with two synthetic hard cuts; both 1.25s and 2.5s cues verified, plus invalid-video rejection. **Real camera footage / production cut accuracy are still NOT_VERIFIED.** X-CLIP contract tests use no model weights on this portable service. |
| Real vision inference | A separate dedicated Render service ran authentic Microsoft X-CLIP weights and independent SigLIP comparisons, including four reviewed video windows. This establishes execution only; the small nonblind data cannot establish production precision/recall or reliable real event onsets. |
| Windows script static checks | 19 source-level contracts on Linux, **not** native PowerShell execution. |

The `report.json` is a report of specific gates, not a substitute for reading the actual `.trx` or for on-device testing. Independent WAV verification checks payload identity, **not** whether the selected sound fits the action or is licensed for commercial work.

## Remaining blockers (do not silently treat as PASS)

**Real-machine and human-dependent:**

1. `scripts/verify-steinberg-windows-foley.ps1 -SelfTest` must run in a **real Windows** process at the same fresh feature SHA. Linux/static checks are insufficient. Status: `NOT_RUN`.
2. Native Windows offline restore/build/MCP-XSD preflight: requires locked NuGet dependencies already cached and, for the complete Foley preflight, independently reviewed sample approval + frame/timing JSON. Status: `NOT_RUN`.
3. Real Cubase 15.0.30+ broker/driver acceptance against an **owned disposable project**, with explicit authorization and verified installation receipt. Newly added dual-mode adapters are not included in the prior upstream live acceptance. Status: `NOT_RUN`.
4. Import the exact `.dawproject` into Cubase/Nuendo and measure actual clip locations, temporal sync with the full video, multichannel handling and save/reopen. The project generates portable files; it does **not** automatically write audio onto a running DAW timeline. Status: `NOT_RUN`.
5. Human listening, legitimate commercial license/attribution checks, and human-confirmed model-window-to-full-video onset mapping. Status: `NOT_RUN`.

**Other non-device capability limits (not proven by the portable gate):**

- GrooveMap requires a correctly installed checkpoint and real source audio; the real authorized model and MIDIVOLT external CLI outputs are not certified end-to-end by this report. Do not infer real external-provider compatibility from source-level adapters.
- **Real CutPilot OpenCV decode has passed on an actual encoded synthetic MP4**, with 90 frames, two expected scene boundaries and independent FFprobe checks. Its source is a **CI-only, byte-exact snapshot** of CutPilot `cutpilot/scenes.py` at `c534cd56e8507288b20c1d55fcb5b8d9e8229ea4` (Git blob `2c4fd2e9a931358f3dc2420b82c63c07d2807ce5`), copied through the user's GitHub connector because unauthenticated raw access returned 404. Production MCP still loads separately installed CutPilot, not this test snapshot. **Real-world footage and cut-accuracy validation remain NOT_RUN**; X-CLIP authentic inference was executed separately on a small sample without dataset-level action-recognition metrics.
- The first Foley feature scope is **door opening/closing candidate suggestions plus manual approval**. It is not generic automatic Foley, automatic sample selection, audio audition, commercial rights verification or DAW host manipulation.
- Mainline support evidence belongs to Cubase 15; **Cubase 12–14 and Nuendo** must not be marketed as validated simply because DAWproject export succeeds in Linux XML tests.

## Version-pinned evidence and pass criteria

Do not describe this candidate as "all work except the device done" unless every remaining non-device external dependency receives its **own** pinned real-input evidence. The precise accurate milestone is **portable Foley packaging + independent audit ready for real-machine acceptance**, with the other gaps above explicitly scoped.

To finish the Windows phase, work only from a fresh checkout of the **feature** branch, with the current HEAD equal to its local `origin/feat/steinberg-dualmode-adapters-20261104` tracking ref. The first narrow step is `-SelfTest`, which needs no Docker, Cubase or NuGet; it can return only `PASS_WINDOWS_SCRIPT_SELFTEST_ONLY`. Proceed to the full human-attested Foley preflight and disposable Cubase/Nuendo import only after the preceding gate succeeds. Source synchronization and approval files must not be fabricated.

**Prohibited claims:** production-ready, Windows-native PASS, real Cubase/Nuendo import PASS, automatic timeline placement, model accuracy validated, independently licensed samples, tested Cubase 12/13/14/Nuendo, or full repository test suite PASS from the 58-case Steinberg-filtered gate.

`main` unchanged. GitHub Actions not used. No workstation Docker required.
