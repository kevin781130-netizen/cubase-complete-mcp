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

- **MIDIVOLT prompt and GrooveMap MIDI Humanize real CLIs PASS** at SHA-pinned sources, and **Beat This! official final0 real inference PASS** on a newly synthesized 24-second drum-stem WAV through GrooveMap's authentic `run_suno_drum_pipeline`. The latter generated tempo, drums, humanized MIDI, groove JSON and real Beat This timing metadata. **No real SUNO stem was tested**; the earlier MIDIVOLT→GrooveMap Humanize test's timing was synthetic and must remain distinguished from the new separately proven checkpoint inference.
- **Real CutPilot OpenCV decode has passed on an actual encoded synthetic MP4**, with 90 frames, two expected scene boundaries and independent FFprobe checks. Its source is a **CI-only, byte-exact snapshot** of CutPilot `cutpilot/scenes.py` at `c534cd56e8507288b20c1d55fcb5b8d9e8229ea4` (Git blob `2c4fd2e9a931358f3dc2420b82c63c07d2807ce5`), copied through the user's GitHub connector because unauthenticated raw access returned 404. Production MCP still loads separately installed CutPilot, not this test snapshot. **Real-world footage and cut-accuracy validation remain NOT_RUN**; X-CLIP authentic inference was executed separately on a small sample without dataset-level action-recognition metrics.
- The first Foley feature scope is **door opening/closing candidate suggestions plus manual approval**. It is not generic automatic Foley, automatic sample selection, audio audition, commercial rights verification or DAW host manipulation.
- Mainline support evidence belongs to Cubase 15; **Cubase 12–14 and Nuendo** must not be marketed as validated simply because DAWproject export succeeds in Linux XML tests.

## Slice 32 — authentic MIDI CLI cross-repo and MCP process evidence (2026-11-04)

The [current Render portable evidence](https://steinberg-dualmode-dotnet-portable.onrender.com/report.json) includes a second, opt-in **real external-provider gate**, enforced in `scripts/verify-steinberg-render.sh`, alongside the previous Foley/CutPilot/XSD tests.

- **GrooveMap:** original repository branch `fix/groovemap-v3-preflight-output-safety-20261102`, pinned HEAD `3b9bc6d5a26212b35c92d91c1fd902061b187440`. Its real `groovemap-next humanize-midi` executable runs from an installed local distribution; no copied/mock provider replaces it.
- **MIDIVOLT:** original repository branch `work/midivolt-ready-real-machine-20261103`, pinned HEAD `0c23471b7508bc84383b7860fb976a0ab1e9cb73`. Its real `midivolt-prompt` command generates MIDI from a textual request using the **deterministic local engine, NOT an AI/LLM model**.
- **Steinberg MCP:** an actual `SteinbergModesTools.ArrangeMidi` process call executes the real MIDIVOLT console entrypoint via an isolated **Linux test-only `.exe` launcher shim** (to match the Windows filename contract). A separate one-case TRX records `ArrangementRealCliTests.ArrangementRealCli01_McpActuallyExecutesInstalledMidivoltPromptAndRejectsOverwrite` **1/1 passed**, independently from the **58/58** regular Steinberg tests.
- **Cross-provider output:** MIDIVOLT generated **168 note events**, including **96 GM drum notes**. The actual GrooveMap MIDI Humanize executable accepted that MIDI and wrote a separate MIDI containing **96 drum events**; original pitch multiplicities and source bytes were preserved, timing/velocity groove transformation observed, and MIDIVOLT refused overwriting an existing output.
- **Compatibility:** the Render static site is configured with officially supported service environment variable `PYTHON_VERSION=3.13.11`; this matches MIDIVOLT `>=3.11,<3.14` and GrooveMap `>=3.11`. No Python interpreter download override, local workstation Docker or GitHub Actions is needed. The setting is scoped to this Render service, not the Git repository.
- **Reproducibility:** both source Git HEADs are checked before installing either package. The portable Gate requires the real subprocess test's TRX and independently reads `.render-steinberg-proof/arrangement-real-e2e/report.json`, rejecting mismatched source SHAs, changed source MIDI, missing groove transform or false claims of Beat This! model execution.

**Exact remaining boundaries:** The separate cross-provider test's timing source remains synthetic. The authentic Beat This! final0 model is now independently verified against a **synthetic drum WAV**, not a SUNO recording; commercial song material and full-song alignment are still `NOT_RUN`. Also not run: real Cubase/Nuendo import, Windows-host process execution, live AI model sound quality or commercial license checks. This is a **real CLI interoperability PASS**, not production-ready DAW automation.

Generated non-personal MIDI examples and test context are in the published `arrangement-real-e2e/` directory of the Render static site.

## Slice 33 — authentic, checksum-pinned Beat This! final0 CPU inference (2026-11-04)

Evidence: [Render portable report](https://steinberg-dualmode-dotnet-portable.onrender.com/report.json), key `real_beat_this_final0`, generated by `scripts/verify_beat_this_real.py`.

- **License:** the Beat This! authors explicitly confirmed the released **code and model weights are MIT-licensed, allowing commercial use and redistribution** in [upstream issue #21](https://github.com/CPJKU/beat_this/issues/21); they warn that copyrighted training recordings might entail additional jurisdiction-dependent risk and cannot offer legal advice ([issue #18](https://github.com/CPJKU/beat_this/issues/18)). Use with attribution; seek specialist advice for commercial product launch as appropriate. The test uses only locally synthesized audio.
- **Weights:** `final0` from a mirror linked and hash-confirmed by the Beat This! maintainer in [upstream issue #20](https://github.com/CPJKU/beat_this/issues/20). SHA-256 `8c328b45f59d8dd3dff219253ff6a8d6482be57d0133a29140e2febbf8eb8331`, actual downloaded file **81,058,141 bytes**. Gate checks SHA before any PyTorch load, refuses wrong bytes, never redistributes the checkpoint.
- **Real runtime:** `beat-this==1.1.0` and CPU-only `torch==2.9.1+cpu` / `torchaudio==2.9.1+cpu`; uses the pinned external GrooveMap source at `3b9bc6d5a26212b35c92d91c1fd902061b187440` and its genuine `BeatThisTracker`, not a fake provider. Auto-download is bypassed by providing the checksum-checked local `.ckpt`.
- **Real model results:** 24 seconds of synthesized monophonic drum WAV at 22,050 Hz; **48 beat timestamps, 47 tempo points, 48 MIDI tempo events, 95 MIDI drum-note events**, plus `_timing.json`, `_drums_humanized.mid`, `_groove.json`. The model reports `beat_source: CPJKU/beat_this:minimal`; original source WAV SHA remains unchanged. These are real predictions **on synthetic audio**, not quality evidence for production SUNO stems.
- **Gate status:** `PASS_REAL_BEAT_THIS_FINAL0_GROOVEMAP_SYNTHETIC_WAV_TO_TEMPO_MIDI_ONLY`. The main `scripts/verify-steinberg-render.sh` requires model SHA, source SHA, real provenance, nonempty validated MIDI/tempo events and an independently present `beatthis-final0/report.json`. No GitHub Actions or local Docker.

**Still NOT_RUN:** (1) actual SUNO/live multitrack audio and quantitative beat/downbeat alignment, (2) Cubase/Nuendo Windows host import and real in-DAW timing review, (3) GrooveMap through the **.NET ArrangeTempo external provider command** on Windows (the real GrooveMap pipeline above was invoked directly in cloud Python), (4) commercial rights review for a final product. Do not infer the separate `arrangement-real-e2e` fixture used real Beat This! merely because the independent `beatthis-final0` gate now does.

## Slice 34 — real MCP ArrangeTempo → GrooveMap → Beat This! final0 (2026-11-04)

**Cloud status: PASS (synthetic input only).** Unlike the prior direct Python model test, this slice invokes the actual .NET MCP `SteinbergModesTools.ArrangeTempo` method, which starts the installed **real** GrooveMap `groovemap-next suno` subprocess. The test-only Linux `groovemap-next.exe` wrapper bridges the production Windows-style executable name; the adapter is not mocked.

- Tested source SHA: `25d509b82405ad7c6dc0380568bf3b98c63224e8`; [portable Render evidence](https://steinberg-dualmode-dotnet-portable.onrender.com/report.json), keys `real_steinberg_mcp_arrange_tempo` and `real_steinberg_mcp_tempo_trx`.
- The same **actual Beat This! final0** pretrained checkpoint is verified by SHA-256 `8c328b45f59d8dd3dff219253ff6a8d6482be57d0133a29140e2febbf8eb8331` before the MCP call. No fabricated checkpoint or inference output.
- Input: 24-second locally synthesized 120-BPM drum WAV, not real SUNO/commercial music. Real MCP output: `_tempo.mid`, `_drums.mid`, `_timing.json`. The independent Python readback found **48 beats, 48 MIDI tempo events and 95 drum note-on events**; timing metadata identified `CPJKU/beat_this:minimal` and `groovemap-next-timing-v2`.
- Security: the exact WAV SHA was preserved; `ArrangeTempo` refused an existing output prefix without changing outputs; absent explicit checkpoint was rejected; no stale `.mcp-inflight` lock remained. The model and outputs were checked independently in `scripts/audit_real_mcp_tempo.py`.
- Mandatory evidence: `ArrangementRealTempoMcpTests.ArrangementRealTempoMcp01_ActualGrooveMapWithFinal0ProducesTempoAndDrumMidi` **1/1 PASS in its own TRX**, checked by the Render Gate. Regular `Steinberg` filter **58/58 PASS** and separate MIDIVOLT real MCP case **1/1 PASS**. Official DAWproject XSD, Foley ZIP negative tests, CutPilot synthetic encoded MP4 and Windows source-contract gates remain required.
- Source boundaries unchanged: this is a **Linux .NET process-to-real-provider test**, not Cubase UI operation, Windows PowerShell execution, or a host driver test.

**Still NOT_RUN:** real SUNO (or legally supplied full-song) audio and quantitative beat/downbeat accuracy, Windows-native invocation of both CLIs, in-host Cubase/Nuendo import/synchronization, and human assessment of resulting music.

## Version-pinned evidence and pass criteria

Do not describe this candidate as "all work except the device done" unless every remaining non-device external dependency receives its **own** pinned real-input evidence. The precise accurate milestone is **portable Foley packaging + independent audit ready for real-machine acceptance**, with the other gaps above explicitly scoped.

To finish the Windows phase, work only from a fresh checkout of the **feature** branch, with the current HEAD equal to its local `origin/feat/steinberg-dualmode-adapters-20261104` tracking ref. The first narrow step is `-SelfTest`, which needs no Docker, Cubase or NuGet; it can return only `PASS_WINDOWS_SCRIPT_SELFTEST_ONLY`. Proceed to the full human-attested Foley preflight and disposable Cubase/Nuendo import only after the preceding gate succeeds. Source synchronization and approval files must not be fabricated.

**Prohibited claims:** production-ready, Windows-native PASS, real Cubase/Nuendo import PASS, automatic timeline placement, model accuracy validated, independently licensed samples, tested Cubase 12/13/14/Nuendo, or full repository test suite PASS from the 58-case Steinberg-filtered gate.

`main` unchanged. GitHub Actions not used. No workstation Docker required.
