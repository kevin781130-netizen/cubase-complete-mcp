# Steinberg dual-mode integration — slice 1 (2026-11-04)

This branch extends the existing **Cubase Complete MCP** server with optional **file-only** adapters. It does not embed, fork, merge, or modify the user's source projects. The upstream MIT license and the existing broker remain intact.

## Modes / precise scope

- **Arrangement (implemented in this slice):**
  - `steinberg_modes_status`: report local provider readiness and both modes.
  - `steinberg_arrange_tempo`: invoke GrooveMap Next V3 `groovemap-next suno --drums <wav> --out <prefix> --device cpu --checkpoint <trusted-local.ckpt>` to generate tempo/drum MIDI and timing JSON. A local checkpoint is mandatory, not auto-downloaded.
  - `steinberg_arrange_midi`: invoke MIDIVOLT's deterministic `midivolt-prompt --prompt <text> --out <file.mid>` to generate an independent, editable arrangement MIDI file.
  - **Implemented in later slices:** metadata-only sample search, sample placement drafts, and portable DAWproject audio-track export. **Still not implemented:** automatic WAV placement in a running DAW, native project modifications, automatic model installation.
- **Foley (spotting draft only):** caller-supplied SFX cue objects become frame-accurate, review-only timecode manifests via `steinberg_foley_spot`. No automatic video analysis, SFX selection/generation or DAW host import yet.

## Source boundaries

- [GrooveMap Next V3](https://github.com/kevin781130-netizen/GrooveMap/tree/fix/groovemap-v3-preflight-output-safety-20261102/groovemap_next) — separate, locally installed Python CLI; the real Beat This! checkpoint and real Cubase validation are still blocked/not run per its RC evidence.
- [MIDIVOLT real-machine candidate branch](https://github.com/kevin781130-netizen/MIDIVOLT/tree/work/midivolt-ready-real-machine-20261103) — separate, locally installed Python CLI; real Cubase/Nuendo host acceptance not claimed.
- [MuseForge](https://github.com/kevin781130-netizen/MuseForge) — already ships an independently hosted MCP server; if used, connect that server directly from the client instead of copying its application into this repository.
- [CutPilot](https://github.com/kevin781130-netizen/CutPilot) — future Foley frame/timecode adapter can reuse file-only video probing and scene-cut logic without importing the whole app.

No vendor checkpoints, pretrained weights, Cubase SDK code, audio samples, or proprietary DAW assets are copied.

## Opt-in configuration (Windows)

Install each upstream tool in its **own** environment. Set environment variables in the same process environment that launches `CubaseCompleteMcp.Server.exe`:

```powershell
$env:STEINBERG_GROOVEMAP_EXE = "C:\\tools\\groovemap\\Scripts\\groovemap-next.exe"
$env:STEINBERG_MIDIVOLT_EXE = "C:\\tools\\midivolt\\Scripts\\midivolt-prompt.exe"
```

These are examples, not assumed installed locations. Only **absolute existing paths** ending in the expected CLI executable name are accepted. No shell execution, implicit install, automatic project changes, or fallback synthesis are performed by the adapter. The provider itself may need an authorized Beat This! checkpoint; the adapter does not bypass missing model resources.

Example tool requests from a connected MCP client:

```json
{"tool":"steinberg_modes_status","arguments":{}}
{"tool":"steinberg_arrange_tempo","arguments":{"drums_wav":"C:\\\\audio\\\\drums.wav","output_prefix":"C:\\\\exports\\\\song01","checkpoint_path":"C:\\\\models\\\\final0.ckpt"}}
{"tool":"steinberg_arrange_midi","arguments":{"prompt":"8 bars in D minor at 110 BPM with chords, bass and drums","output_midi":"C:\\\\exports\\\\song01_sketch.mid"}}
```

For GrooveMap, pass an existing trusted local `.ckpt` file through `checkpoint_path`. Missing checkpoints fail closed; the adapter does not attempt remote model downloads. A successful GrooveMap call requires MIDI structure and `groovemap-next-timing-v2` metadata with `beat_source: CPJKU/beat_this:minimal`, rejecting its known explicit test stub. This is evidence of the declared provider path, **not** proof of model authenticity, license, or timing accuracy.

The target output directory **must already exist**. Output files are never overwritten. A process/inference error is reported as an error; files are not marked verified until expected MIDI headers are observed. An interrupted call can leave partial files requiring manual inspection.

## Slice 3 — local sample library (file-only, no host mutation)

The additional `SteinbergSampleTools` MCP class exposes:

- `steinberg_sample_search`: bounded scan of a **user-supplied absolute library root**, optional category and target BPM. Results include absolute file paths, category **guesses from filename or parent folder**, and BPM **only when the filename explicitly includes `120bpm`-style metadata**. WAV / AIFF / FLAC extensions only; symlinks and reparse-point entries are skipped. There is **no listening, waveform tempo estimation, musical key detection, source-license validation or sample preview**. At most 12,000 entries and 400 folders, eight nested levels, 100 results.
- `steinberg_sample_plan`: explicit selection of **one existing sample under the approved library root**; file signature and SHA-256 verification (up to 256 MiB), and a new `.json` placement draft with constant project BPM, musical bar/beat/sixteenth, 960 PPQ tick and time in seconds. Refuses any pre-existing manifest; no host or source audio is changed.
- `status: draft_requires_approval` and `host_action: none` are deliberate. **No automatic placement in Cubase or Nuendo**, no audio time stretching, and no implied usage rights.

MCP calls (illustrative paths; replace with actual local folders):

```json
{"tool":"steinberg_sample_search","arguments":{"library_root":"C:\\\\AudioLibrary","category":"kick","target_bpm":120,"limit":20}}
{"tool":"steinberg_sample_plan","arguments":{"library_root":"C:\\\\AudioLibrary","sample_path":"C:\\\\AudioLibrary\\\\Kick\\\\kick_120bpm.wav","output_manifest":"C:\\\\Exports\\\\kick-bar2.json","project_bpm":120,"bar":2,"beat":1,"sixteenth":0,"beats_per_bar":4}}
```

Source search only reads metadata and bounded directory entries; plan reads a selected file for its hash but does not copy or decode it. Samples must be properly licensed for the user's intended use.

**Verification:** a regression-test source file covers discovery, ordering, beat/tick/second arithmetic, collision refusal, outside-root rejection and invalid audio signatures. A real `.NET 8` build and test execution are **NOT_RUN** in this GitHub editing environment. An actual Cubase/Nuendo timeline import remains a separate future task.

## Slice 4 — DAWproject multitrack export (file-only)

New MCP tool: **`steinberg_export_dawproject`**.

This is an additive, portable-file workflow: the user first makes `steinberg_sample_plan` drafts, reviews the sample and timing information, then explicitly sets `approved=true` to package 1–64 plans into a new `.dawproject` ZIP with **`project.xml`**, **`metadata.xml`**, and bundled WAV media.

- WAV **RIFF PCM or IEEE-float, mono/stereo** only in this first cut. AIFF/FLAC remain discoverable by the library search but are **not** yet exportable. RF64, WAVE_FORMAT_EXTENSIBLE, codecs and implicit transcoding are excluded.
- All plans must have the same constant BPM and 4-denominator meter, 960 PPQ, valid bar/beat/sixteenth timing and exact source SHA-256. The tool refuses missing/changed samples, mixed tempo, unsupported formats and existing output files.
- Each filename-derived category gets its own DAWproject audio track, with independently positioned clips. Repeated use of the same verified WAV reuses one ZIP media entry. The converter does **not** time-stretch BPM-tagged loops, generate audio, calculate new BPM, or touch an open Cubase/Nuendo project.
- Bundling WAV audio **copies** the approved samples into the portable file. Users must have appropriate licensing/rights; neither this tool nor the source repositories certify media redistribution rights.
- A new output path is mandatory; a staging ZIP is used, then renamed without overwrite.
- Host import is still **manual**: File → Import → DAWproject. Steinberg documents original-time-position audio imports for Cubase 15 and Nuendo 15. Cubase Pro/Artist 14.0.20+ supports DAWproject. Cubase/Nuendo 12–13 require a separate old-version interchange route; older host versions and exact DAW timing remain **NOT_RUN**, not supported-by-test.

Example MCP call (Windows; replace all paths):

```json
{
  "tool": "steinberg_export_dawproject",
  "arguments": {
    "library_root": "C:\\\\AudioLibrary",
    "manifests": [
      "C:\\\\Exports\\\\kick-bar2.json",
      "C:\\\\Exports\\\\snare-bar4.json"
    ],
    "output_dawproject": "C:\\\\Exports\\\\arrangement-v1.dawproject",
    "approved": true
  }
}
```

A new unit-test source covers multi-track grouping, three sample events, source-file deduplication, timing, missing approval, overwrite refusal, mismatched BPM, and source SHA mismatch.

**Unverified gates:** No `.NET 8` compiler/test runner was available during the repository edit; `dotnet build`, `dotnet test`, validation against Bitwig's `Project.xsd`, and Cubase/Nuendo real import/playback are **NOT_RUN**. A ZIP containing XML/media is not automatically a verified importable native DAW session.

Upstream DAWproject specification: https://github.com/bitwig/dawproject
Official Cubase import reference: https://www.steinberg.help/r/cubase-pro/15.0/en/cubase_nuendo/topics/exchanging_files_with_other_applications/exchanging_files_with_other_applications_importing_dawproject_files_t.html
Official Nuendo import reference: https://www.steinberg.help/r/nuendo/15.0/en/cubase_nuendo/topics/exchanging_files_with_other_applications/exchanging_files_with_other_applications_importing_dawproject_files_t.html

## Slice 5 — pinned official XSD and Windows offline preflight

Official DAWproject 1.0 schemas are included as **test-only fixtures**, not production dependencies:

- `tests/DawprojectSchema/Project.xsd`
- `tests/DawprojectSchema/MetaData.xsd`
- `tests/DawprojectSchema/LICENSE.bitwig.txt`

Source: `bitwig/dawproject` commit `ee4dcdde75940f30e14e55401a26955a58b8322b` (MIT). Do not silently replace the pinned schemas with current upstream files.

`SteinbergDawprojectSchemaTests` exports an actual 1-track WAV fixture through the project's own `SteinbergSampleTools.Plan` + `SteinbergDawprojectTools.Export`, then validates `project.xml` and `metadata.xml` with `System.Xml.Schema`. It also has a negative case to confirm malformed XML is rejected.

On Windows with a compatible .NET 8 SDK, from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\verify-steinberg-offline.ps1
```

The script performs a **strictly offline, locked restore** using an empty local NuGet feed plus the already populated global package cache. Missing cached packages are reported as `ENVIRONMENT_BLOCKED` (no network fallback); it then builds the solution, runs all `Steinberg`-named tests and the pinned XSD validation. A machine-readable report is written under the user's temporary directory. It does not call GitHub Actions, upload evidence, run Cubase/Nuendo, or claim real timeline acceptance.

**Current truth:** The agent's execution container has **no `dotnet` executable** and cannot run this Windows-targeted .NET solution. Accordingly the new .NET build and pinned official XSD tests are **NOT_RUN** here; source/test-file presence is not a PASS. A report of `PASS_OFFLINE_ONLY` is issued **only** after the script really returns success.

## Slice 6 — source-safety hardening before real .NET acceptance

The WAV reader now rejects a false RIFF byte count, truncated/misaligned chunk padding, duplicate `fmt`/`data` chunks, unsupported sample formats, and contradictory channel count / sample size / byte rate / block alignment. `DAW06` regression cases cover three corruptions and then assert that the same approved original WAV can still be packaged. These tests are committed but **NOT_EXECUTED** in this environment without the .NET SDK. No actual Cubase/Nuendo host import has run.

## Slice 11 — Foley video frame-to-SFX cue spotting (metadata-only)

**MCP tool**: `steinberg_foley_spot` (implemented in `SteinbergFoleyTools.cs`).

- Input: an existing local video file path (MP4, MOV, MKV or MXF), *caller-supplied* frame-rate numerator and denominator, *caller-supplied* total frame count, and a JSON **array of cue objects**. The video is **not opened or decoded**. Header/codec, actual FPS and frame count are **not verified**.
- Cue schema: `frame` (integer, **zero-based absolute frame index**), optional `duration_frames` (integer, default 0), `event_label` (plain text), `track` (one of footsteps, impacts, cloth, doors, ambience, vehicles, other), `sfx_query` (search phrase for a human or future external audio library matcher).
- Converts each input frame to seconds using `frame * fps_denominator / fps_numerator`, rounded to 9 decimals for the review sheet. Handles fractional video rates such as 24000/1001, 30000/1001 and 60000/1001. This is **not drop-frame or SMPTE timecode parsing**; no timeline start offset or variable-FPS conversion is performed.
- Output: **new** `steinberg-foley-cues-v1` JSON file with chronologically sorted observations, original frames and input indices, matching queries, `sample_path: null`, `selection_status: unmatched_requires_review`, `status: draft_requires_approval` and `host_action: none`. Never overwrites prior files. Does not silently invent clips or sound samples.
- Limits: 1–128 cues per call, up to 64 KiB JSON, frames must stay inside the declared video range. Actual detection, frame probing, sample search, matching, licensing, and seconds-based DAWproject audio placement belong to independent future integrations. Optional CutPilot or visual detectors can generate the **input** cue JSON without copying them into this repository.

Example (one event at absolute frame 120 in a 30000/1001 fps video):

```json
{
  "tool": "steinberg_foley_spot",
  "arguments": {
    "video_path": "C:\\\\Video\\\\scene.mp4",
    "output_manifest": "C:\\\\Exports\\\\scene-foley-cues.json",
    "fps_numerator": 30000,
    "fps_denominator": 1001,
    "video_frame_count": 5000,
    "cues_json": "[{\\\"frame\\\":120,\\\"duration_frames\\\":12,\\\"event_label\\\":\\\"Door closes\\\",\\\"track\\\":\\\"doors\\\",\\\"sfx_query\\\":\\\"wood door slam\\\"}]"
  }
}
```

The source can be a human spotting pass or an *independent* computer-vision detector. This slice does not run or claim such detection. A draft is not a playable DAW session.

**Current gate**: New `SteinbergFoleyTests` are committed; re-run the Linux Render .NET build/tests on the exact new SHA before reporting PASS. Windows Cubase/Nuendo remains NOT_RUN.

## Slice 12 — optional CutPilot scene detector bridge (automated picture cuts, **not** action recognition)

- **Actual detector**: [CutPilot `cutpilot/scenes.py`](https://github.com/kevin781130-netizen/CutPilot/blob/fix/rc-core-blockers-20261102/cutpilot/scenes.py) — its `detect_scene_cuts(video_path,start,end,...)` samples video frames with OpenCV HSV histogram correlation. **It does not identify walking, doors closing, footsteps, punches or audio/SFX.** No CutPilot code or model was forked/copied into this MCP project.
- The new `steinberg_foley_cutpilot_scenes` MCP command launches the **separately installed** CutPilot Python environment with a bundled lightweight `scripts/cutpilot_foley_scene_provider.py` adapter. It reads `ffprobe`'s actual rational average video FPS and `nb_frames`, requires a nominal/average FPS match, then asks the installed CutPilot detector for scene boundaries. Cut timestamps are converted to approximate frame positions, rounded to the declared frame grid. The **sample interval (default 0.25 s) is the true accuracy limit**; the output is *not* precise SMPTE frame or verified CFR/variable-rate timing.
- The result is a **new** `steinberg-foley-cues-v1` draft from `SteinbergFoleyTools.SpotCutPilot` with `source: cutpilot_histogram_scene_boundaries_not_action_detection`, `track: other`, `sample_path: null`, `selection_status: unmatched_requires_review`, and `host_action: none`. No sound files or effects are chosen. If no scene boundaries are found, no output is written.
- Explicit opt-in is required. Configure `STEINBERG_CUTPILOT_PYTHON` and `STEINBERG_CUTPILOT_FFPROBE` to **absolute paths of local executables**; pass an absolute path to the existing CutPilot checkout as `cutpilot_root`. Python must have `opencv-python-headless` or equivalent working `cv2` installed. The bridge installs **nothing** and uses no shell, no remote API, no model weights, and no DAW SDK. A scan has a 120-second timeout; the provider caps footage at ten minutes per call and refuses sources without reported frame count or with inconsistent reported FPS.

Example configuration (replace paths and use the Python interpreter that already has CutPilot's `cv2` dependency):

```powershell
$env:STEINBERG_CUTPILOT_PYTHON = "C:\\CutPilot\\.venv\\Scripts\\python.exe"
$env:STEINBERG_CUTPILOT_FFPROBE = "C:\\ffmpeg\\bin\\ffprobe.exe"
```

Example MCP parameters:

```json
{
  "tool": "steinberg_foley_cutpilot_scenes",
  "arguments": {
    "video_path": "C:\\\\Video\\\\scene.mp4",
    "cutpilot_root": "C:\\\\CutPilot",
    "output_manifest": "C:\\\\Exports\\\\scene-visual-cut-review.json",
    "sampling_interval": 0.25,
    "threshold": 0.52
  }
}
```

**Acceptance boundary**: Tests in C# verify the MCP contract and marked CutPilot provenance. Five Python provider tests verify rational FPS, refusal of VFR/range errors and the external module call using a **fake/simulated CutPilot detector**. These are *contract tests only*, not a real OpenCV video-run. Real video samples, Windows path handling, true semantic action recognition, SFX matching and Cubase/Nuendo timeline placement are **NOT_RUN**.

## Slice 13 — local X-CLIP semantic video *candidate* classifier (opt-in)

New MCP entry: `steinberg_foley_xclip_actions` in `SteinbergVisionActionTools.cs`.

- Model: [Microsoft X-CLIP base patch32](https://huggingface.co/microsoft/xclip-base-patch32), marked **MIT** on its model card. It compares **eight decoded RGB frames per short video window** to a fixed set of ten text prompts: walking, running, opening/closing a door, collision, clapping, car driving, fabric handling and two neutral/abstention prompts. This is real *video-to-text similarity inference* when the model is installed; it is **not** pixel-perfect contact tracking, guaranteed action recognition, object tracking, acoustic detection or proof of any audible event.
- Explicit local model weights are required, with a `config.json`, processor files and `model.safetensors`. The loader uses `XCLIPModel` / `XCLIPProcessor`, **`local_files_only=True`, `use_safetensors=True`, `trust_remote_code=False`**, plus offline environment flags. No implicit model download, pip install, GPU requirement, remote video upload, shell invocation or internal CutPilot copy.
- Each invocation handles **at most 30 seconds**, up to **12 windows**. Video decoding uses OpenCV; `ffprobe` supplies explicit rational FPS and frame count. An average/nominal FPS mismatch is rejected as potential VFR; agreement is **not** proof of CFR. Candidate positions use the **window center**, not true onset. The fixed vocabulary can miss or confuse actions.
- Normalized scores are **relative to these ten prompts, not calibrated probabilities**. A non-neutral candidate is emitted only when both `min_relative_score` (default 0.48) and `min_margin` (default 0.12) are exceeded. Every source cue records the window limits, class and score. Ambiguous or neutral windows abstain. No candidates means `NoActionCandidates` and **no manifest** is created.
- The `steinberg-foley-cues-v1` sheet remains **`draft_requires_approval`**, with `classification_status: unverified_video_text_similarity`, `selection_status: unmatched_requires_review`, `sample_path: null`, `host_action: none`. It is not a Cubase project. No sample license, actor/person identity, scene audio, or native DAW interchange is inferred.

Before use, install Python with matching `torch`, `transformers`, `opencv-python` and image/processor dependencies in a separate local virtual environment. Pre-download model weights only after checking model licensing and source provenance; select the folder explicitly. Example Windows settings:

```powershell
$env:STEINBERG_XCLIP_PYTHON = "C:\\Tools\\XCLIP\\Scripts\\python.exe"
$env:STEINBERG_XCLIP_FFPROBE = "C:\\ffmpeg\\bin\\ffprobe.exe"
```

Example MCP arguments:

```json
{
  "tool": "steinberg_foley_xclip_actions",
  "arguments": {
    "video_path": "C:\\\\Video\\\\short-clip.mp4",
    "model_directory": "C:\\\\Models\\\\xclip-base-patch32",
    "output_manifest": "C:\\\\Exports\\\\short-clip-xclip-review.json",
    "min_relative_score": 0.48,
    "min_margin": 0.12
  }
}
```

**Verification boundary:** the Render Linux gate compiles .NET and tests the MCP schema, source window/score preservation and fail-closed settings, plus six dependency-free Python tests with a **simulated classifier**. It **does not download or run the actual 0.2B-parameter model**, and does not test native Windows, commercial acceptance of the training data, accuracy on real footage or Cubase/Nuendo. Do not call these contract tests a real model-inference PASS.

## Slice 14 — real pretrained X-CLIP score diagnostics (3 weakly labeled Commons videos)

The X-CLIP provider now accepts an **explicit diagnostic-only** `--diagnostic-scores` CLI flag. The normal MCP action-candidate tool and existing JSON outputs are unchanged. Diagnostics record the **ten unnormalized model logits**, **ten relative softmax values**, rank order, top-class score and margin, frames sampled, source window and explicit abstention reason. None of these are calibrated event probabilities or action onset timestamps.

The dedicated Render real-weights build additionally runs `scripts/benchmark_xclip_commons.py` and publishes a separate `diagnostic-report.json` with per-case source SHA-256, exact model/SHA source inherited from the main report, class ranking and counts for three **Wikimedia Commons** videos. The original and derived media files are deleted before publish; only provenance, scores and statistics appear publicly.

Samples, description-based **weak** labels and licensing:

- `walking`: [Vespero en Hong Kong Island (2014) 02.webm](https://commons.wikimedia.org/wiki/File:Vespero_en_Hong_Kong_Island_(2014)_02.webm), creator RG72, **CC BY-SA 4.0**
- `running`: [Runners.webm](https://commons.wikimedia.org/wiki/File:Runners.webm), creator Evworo, **CC BY-SA 3.0**
- `clapping`: [Viking clapping of Esteghlal fans in the match against Shahr Khodro, 9 May 2022.webm](https://commons.wikimedia.org/wiki/File:Viking_clapping_of_Esteghlal_fans_in_the_match_against_Shahr_Khodro,_9_May_2022.webm), creator Maometto, **CC BY-SA 4.0**

Original videos are downloaded temporarily, clipped to the **first two seconds**, and transcoded to 12 fps for deterministic 8-frame windows. Their **Commons titles/descriptions are not frame-level ground truth**; a selected two-second excerpt may not contain the named action. Thus "possible misses" and Top-1/Top-3 description agreement are exploratory diagnostic counts **not validated recall, false-negative rate, precision, or commercial accuracy**. The normal 0.48/0.12 threshold is not changed based on three samples; report-only sweeps compare alternative settings. No KTH research-only video was used, no generated SFX was selected, and Cubase/Nuendo real-machine import remains NOT_RUN.

## Slice 15 — checksum-anchored visual review of first-two-second benchmark clips

We inspected six sampled frames (indices 0, 5, 9, 14, 18 and 23) **independently from the video filenames** and recorded the review in `tests/fixtures/xclip_visual_review_v1.json`. Each review is locked to the exact first-two-second contact-sheet SHA-256; the real-video gate refuses changed frames until a new review is performed.

- **`crowd_walking` / walking: confirmed visible within sampled stills.** A foreground pedestrian advances toward the camera across sampled frames. The real X-CLIP class was ranked #2 and abstained at the existing 0.48/0.12 production threshold; this is one *reviewed positive* with a **possible missed candidate**, not a verified temporal recall percentage.
- **`runners` / running: not confirmed.** The sampled first-two-second frames show athletes on a running track, including people in crouched start positions. They do **not independently confirm running within that excerpt**. Excluded from reviewed-label denominator; its earlier "possible running miss" must **not** be used as a false negative.
- **`fans_clapping` / clapping: indeterminate.** Raised arms are visible, but a repeated clap/contact sequence cannot be established from six stills. Excluded from reviewed-label denominator; the earlier X-CLIP match is not verified as correct.

These are **human-reviewed static excerpts only**. The review was **not blinded** to previously recorded X-CLIP rankings. Neither full-frame motion nor frame-level onset timing nor sound truth was established. Only **one of three** original weak-label cases is sufficiently visually confirmed for the narrow descriptive subset. With a sample of one, no production accuracy, precision/recall, false-negative rate or new threshold is claimed.

The `benchmark_xclip_commons.py` gate now preserves its original weak-label figures for comparison, but additionally reports `human_frame_review_summary` where the unconfirmed two cases are excluded. Media and contact sheets are not published to the static site; only metadata, frame hash and review text are saved. The model, production settings, DAWproject export and host communication remain unchanged.

## Slice 17 — new real-video source audit and fourth X-CLIP benchmark case

An additional bounded candidate survey (`scripts/survey_foley_action_videos.py`) reviewed **three new** Commons clips from separate licensing sources; all downloaded media and contact images were deleted after the checksum-locked six-frame review. The survey is **opt-in** and not rerun on every routine model gate. Evidence and exclusion rationale are preserved in `tests/fixtures/xclip_candidate_survey_20261104.json`:

1. **Beach walking — visually confirmed**: [Walking in the sands.webm](https://commons.wikimedia.org/wiki/File:Walking_in_the_sands.webm) by sgu18ify (CC BY 3.0). A person advances along the shoreline across six sampled frames. Included as the **fourth** `benchmark_xclip_commons.py` case; the exact contact-sheet SHA-256 `ec3ca9a352b05d0a7618edf38a8af5c85235c8292181f583455fa25779d41210` is required.
2. **CDC child running — not independently confirmed in selected segment**: [2 Year Milestone- Begins to run.webm](https://commons.wikimedia.org/wiki/File:2_Year_Milestone-_Begins_to_run.webm) (CDC, public domain). The first two seconds show a child carrying a ball and moving; a definite run is not established from the stills. Excluded, regardless of the source title.
3. **NHS clapping — not visible in selected segment**: [Clap for NHS Thousands clap hands together...webm](https://commons.wikimedia.org/wiki/File:Clap_for_NHS_Thousands_clap_hands_together_in_round_of_applause_for_NHS_workers_Metro.webm) (AZ News, CC BY 3.0). First two seconds contain a graphic intro; no visible clapping. Excluded.

The real X-CLIP gate now requires **four** benchmark clips with exactly **two** hashed, visually confirmed walking positives, and continues to exclude the two unresolved original labels. It must not assert any population-level precision/recall, onset timing, background-noise detection or commercial accuracy from these few static-frame confirmations. The fixed production threshold (0.48 / 0.12) and Cubase/Nuendo core remain unchanged. Windows real-machine validation and sample matching remain NOT_RUN.

## Slice 18 — independent Apache-2.0 still-image model baseline (SigLIP)

Optional validation-only `scripts/compare_siglip_xclip.py` compares **real pretrained Google SigLIP base patch16-224** to existing **real Microsoft X-CLIP** on the exact same eight video-frame indices and the same ten text descriptions. The SigLIP model card labels the weights **Apache-2.0**; the source weights are pinned to commit `88ce39778b70932de29ec01792536fc86c2cfdbf`, SHA-256 `2c63cb7d1f2e95ba501893cbb8faeb4ea9a3af295498d35097126228659c2af8`. Only `safetensors` are accepted and model execution occurs locally within the isolated Render build.

Important: **SigLIP is not a temporal action-recognition model**. It scores eight individual still frames separately; its comparison ranking averages eight independent sigmoid similarities. X-CLIP consumes the video frames as a temporal sequence, scoring them jointly. Each model's score definition differs; the scores and thresholds must **never** be compared numerically or used to automatically select/emit Foley audio.

The comparison gate re-downloads the same four attributed Wikimedia Commons files, demands the exact previously proven source **and reencoded video SHA-256** for each one, evaluates eight corresponding frame indices, and deletes all temporary footage. The resulting `siglip-comparison.json` records per-class SigLIP frame scores and side-by-side **rankings** with X-CLIP. It reports the two visually reviewed walking positives separately from two unverified action labels. No precision, recall, action-onset accuracy, sample-placement or live Cubase/Nuendo claim is made. No weights or videos are published. This step does not change the deployed Foley classification pipeline.

## Slice 19 — source-audited running, door and no-clapping controls (not a model-score gate)

An optional bounded review survey is implemented at `scripts/survey_foley_running_door_controls.py`, run in the separate real-model Render validation service. No change to MCP production logic. It downloads three Commons source videos temporarily, samples each at **0–2 and 4–6 seconds**, reencodes each to 12fps, checks contact-frame digest/clip SHA-256, then removes all source and derived media before publishing. **Do not count a file title or category as evidence that the target action occurs in those windows**. Each sampled contact sheet appears only in ephemeral build logs for independent review.

Sources and attribution:

- **Running candidate:** [Jogging – near Arakawa River – Tokyo, 2022](https://commons.wikimedia.org/wiki/File:Jogging_-_near_arakawa_river_-_tokyo_japan_-_2022_may_3.webm) by Nesnad, CC BY 4.0
- **Door-close candidate:** [R211 Door Closing](https://commons.wikimedia.org/wiki/File:R211_Door_Closing.webm) by Schvaxet, CC BY-SA 4.0; vehicle-door movement might be visually different from household doors
- **Door-open candidate:** [Idrija Mine Opening and Closing Door](https://commons.wikimedia.org/wiki/File:IDRIJA_ANTHONY%60S_SHAFT%3B_OPENING_AND_CLOSING_THE_DOOR_TO_THE_MINE.webm) by Sounds of Changes, CC BY 3.0
- **Narrow negative control, previously reviewed:** first 2 seconds of the CC BY 3.0 *Clap for NHS* video (AZ News) show animated intro/graphics, **not visible clapping**. This is a **no-clapping** candidate, **not** proof of no movement, no visual events or no Foley-relevant audio.

Output `action-controls-survey.json` records source licenses/credits, individual 2-second window indices, media SHA-256 and contact-sheet checksums; `visual_annotation=PENDING_FOLLOW_UP_REVIEW`, `real_xclip_siglip_on_new_controls=NOT_RUN`. A download/fail-closed or insufficient candidate count is reported, not silently changed to PASS. Only after six-frame contact sheets are inspected should observed positives or negatives be added to a subsequent model comparison. The original 4-video benchmark remains unchanged until then.

**Independent sampled-still review completed 2026-11-04** (evidence: `tests/fixtures/foley_action_controls_visual_review_20261104.json`, every case bound to its contact-sheet SHA-256; Render survey SHA `0529d272`). The 4–6 second **Arakawa jogging** excerpt visually supports a runner; the 4–6 second **R211 subway** excerpt visibly shows door panels moving toward closure; the 0–2 second **Idrija mine** excerpt shows a person opening a hinged door. These are three visually supported action clips, **not yet run through X-CLIP or SigLIP**. The 4–6 second mine-door excerpt shows an already-open door with no discernible opening or closing in the sampled images: a **door-operation-specific negative**, not a universal no-motion/no-SFX negative. The other two examined windows remain indeterminate. All six observations are based on non-blind review of sampled stills, not verified onset times or full-motion ground truth.


## Slice 20 — real paired inference on audited Foley controls (Render evidence)

The dedicated Render X-CLIP+SigLIP build now runs `scripts/benchmark_foley_reviewed_controls.py` **after** the existing four-video sanity gate, using separately verified official local model weights. Four, and only four, 12fps two-second video windows from `tests/fixtures/foley_action_controls_visual_review_20261104.json` are independently re-downloaded, reencoded and verified against the exact previously reviewed **six-frame contact JPEG SHA-256** before any model inference:

- Arakawa jogger at 4–6 s — visually supported `running` positive
- R211 subway doors at 4–6 s — visually supported `closing_door` positive
- Idrija mine door at 0–2 s — visually supported `opening_door` positive
- Idrija mine door at 4–6 s — **class-specific negative only** for discernible `opening_door` / `closing_door`; no claim of overall inactivity or absence of audio

Each model runs real pretrained weights in a **separate CPU process** to limit memory. X-CLIP analyzes 8 frames jointly and reports all 10 raw logits, relative softmax ranks, whether the fixed **0.48 / 0.12** production thresholds emit a matching candidate, and whether a door-specific negative wrongly receives a door action candidate. SigLIP scores the exact same 8 frames **independently** and aggregates ranks; its sigmoid values are **not calibrated** or comparable to X-CLIP scores or gating thresholds. Results are separated into `action-controls-xclip.json`, `action-controls-siglip.json` and `action-controls-comparison.json` with source hashes, credits, reviewed JPEG hashes and a narrow negative-control field. All four video clips are deleted before static-site deployment.

**No claim of verified population precision or recall** is permitted: the labels rely on non-blinded six-frame still-review, not exhaustive temporal annotations; the negative controls are narrow; the dataset is just three positives and one negative. The reported model ranking / emission count serves only to decide future technical development and cannot change the production threshold automatically. No SFX sample is selected, no commercial video-quality guarantee is inferred, and Cubase/Nuendo Windows import remains NOT_RUN. A real Render `LIVE` deployment and valid JSON reports on the exact SHA are required before the new model comparison can be called PASS.

## Slice 21 — conservative two-model door-review fusion (no automatic SFX)

The independent `scripts/foley_door_fusion.py` module adds an **experimental, review-only** decision on verified X-CLIP and SigLIP results. It does not modify either model, the production X-CLIP 0.48/0.12 thresholds, the Cubase MCP server or any DAW session.

**Rule:** for `opening_door` or `closing_door`, **both models must rank the exact same directional door action first** in the exact same video window (video and reviewed-contact SHA-256 must match). Otherwise the fused gate abstains. This suppresses the observed stationary mine-door SigLIP opening-door false-positive, because X-CLIP's first-ranked class was `quiet_scene`. It also suppresses X-CLIP/SigLIP directional disagreements, including the subway-door close clip where the video-model's `closing_door` rank was 7. The mine-door **opening** positive had matching opening-door Top-1 rankings and therefore becomes a **review candidate**, even though X-CLIP's unchanged production score/margin gate did not emit an action. This fusion **does not verify physical door motion**; both models can still be wrong in an unseen scene. It must not automatically place a sound.

Every resulting row says `status: review_proposal_not_confirmed` or `abstained`, `verified_motion: false`, `confidence_calibrated: false`, `selected_audio_file: null`, `sound_effect_auto_placement: false`, and `host_action: none`. The `door_fusion_experimental` summary in `action-controls-comparison.json` is generated only from real model rankings, not from reviewer class labels. A strict gate requires the hash-matched class-specific static-door negative to produce **no** door action. Seven offline contract tests also cover model disagreement, spoofed rankings, mismatched video hashes, no-Daw/no-SFX behavior and a negative-control regression. This is **diagnostic only**; it does not claim action accuracy, production readiness, temporal tracking or motion onset localization.

## Slice 22 — additional OpenCV door-area time-series motion guard

An **experimental** local OpenCV guard is implemented in `scripts/foley_door_motion.py` and checked on real source clips through `benchmark_foley_reviewed_controls.py motion`. This is **not an autonomous door-object detector**. The user/reviewer must select a localized, normalized `[x0,y0,x1,y1]` door ROI independently of the classifier's guess, and the ROI must be bound to the same source contact-sheet SHA-256. Three approximate, manually inspected ROIs for the two moving-door clips and one stationary-door clip are held in `tests/fixtures/foley_door_motion_rois_v1.json`.

For each clip, the detector decodes **eight ordered video frames**, compares smoothed grayscale intensity between adjacent frames, and counts pairs with localized pixel changes in the specified ROI. Two independently changing pairs are required. A background-change check suppresses broad global brightness/camera changes. This is a **motion proxy only**: reflections, people, camera parallax or unrelated activity within the door ROI can still trigger it; low-contrast door motion can be missed. It **does not segment door panels, establish opening versus closing direction, verify real hinges, or localize a sound event to a specific frame**.

The second guard is strictly fail-closed: **first** X-CLIP and SigLIP must agree on the exact Top-1 directional door class, **then** matching source-locked ROI motion evidence must exist. Missing/changed ROI evidence, stationary ROI, or disagreeing models produce no door proposal; outputs remain `review_proposal_not_confirmed` or `abstained`, with `selected_audio_file: null` and `host_action: none`. Running and other sound classes are unaffected. The final report contains `door_roi_motion_experimental` and a separate `action-controls-motion.json` showing all frame-pair measurements, while the prior untouched two-model fusion result remains available for comparison.

**Gate**: eight synthetic/unit cases plus the existing real 4-clip X-CLIP/SigLIP acceptance. The real static-mine-door negative must have `localized_motion_evidence: false`; else fail rather than silently accepting the implementation. No production Foley sample matching, Cubase host changes or Windows acceptance is claimed. If the real motion gate fails, `ENVIRONMENT_BLOCKED` or `TEST_FAILED` must be reported rather than declaring PASS.

## Slice 23 — review-only local Foley sample candidate search

A new MCP tool, `steinberg_foley_sample_candidates` in `SteinbergFoleySampleTools.cs`, joins existing **bounded local sample discovery** (`steinberg_sample_search`) to the conservative `action-controls-comparison.json` source produced by real X-CLIP/SigLIP plus reviewed door ROI motion. It works on a **downloaded local copy** of the report; model weights or Render network access are not needed for this tool. The new optional `filename_query` argument to `steinberg_sample_search` filters sample filenames and their immediate parent-folder names **before** the existing 100-result limit. The existing public search behavior is unchanged when the query is omitted.

Example MCP arguments (Windows; these are example paths, not actual user folders):

```json
{
  "fusion_report": "C:\\\\Foley\\\\action-controls-comparison.json",
  "library_root": "D:\\\\Samples",
  "output_manifest": "C:\\\\Foley\\\\foley-sample-review.json",
  "rights_registry": "D:\\\\Samples\\\\sample-rights.json",
  "max_per_event": 3
}
```

Only `door_roi_motion_experimental.motion_gated_rows` with `status: review_proposal_not_confirmed`, the exact action `opening_door` or `closing_door`, source video/contact SHA-256 and `host_action: none` can produce suggestions. Abstained/negative windows produce **zero** candidates. The input manifest is still a **model output, not proof of a verified or human-approved action**. This pass only ranks local WAV/AIFF/FLAC **filenames and parent-folder names** containing door/gate/hinge/latch terms. It rejects contradictory open+close filename tags and does not identify sounds by listening. Each category's search is bounded to 100 results (scan limit 12,000 entries; max 400 directories; 8 levels) and **`search_truncated_at_100_per_keyword: true`** signals incomplete discovery.

Optional, explicitly provided local rights registry format:

```json
{
  "schema": "steinberg-local-sample-rights-v1",
  "entries": [
    {
      "relative_path": "Doors/wood_door_open.wav",
      "license_id": "CC0-1.0",
      "source_url": "https://example.org/licensing/wood_door",
      "attribution": "Example only; not evidence of a real grant"
    }
  ]
}
```

This attaches only `user_declared_not_verified` rights evidence, **not confirmed legal permission**. Without a record, each candidate says `no_rights_record`. Every exported record keeps `selected: false`, `chosen_sample_path: null`, `audio_content_decoded: false`, `audio_content_sha256_verified: false`; the sheet is `draft_requires_human_approval` and `host_action: none`. No DAW timeline, source audio, Cubase session or installed plugins are mutated, and no sound is copied/exported. The next boundary is **user review + rights/format/audio validation**, not automatic placement.

## Slice 24 — bounded audio format, duration and rights preflight (review only)

The `steinberg_foley_sample_candidates` MCP tool now runs **basic RIFF/WAV header integrity and duration preflight** on locally discovered Foley candidates before ranking. A new, dependency-free parser (`SteinbergFoleyAudioProbe.cs`) checks RIFF declared file length, bounded RIFF chunks, one `fmt ` and one `data` chunk, PCM or IEEE-float encoding, consistent channel count (1–8), sample rate (8–192 kHz), bit depth (8/16/24/32 for PCM; 32 for float), `blockAlign`, `byteRate`, and whole-sample data size. It calculates duration from actual `data` chunk bytes and declared byte rate, not from filename tags.

The optional `max_duration_seconds` parameter defaults to **20 seconds** (supported 0.1–120 seconds). Candidates shorter than 0.05 seconds, longer than the requested maximum, truncated, malformed or using unsupported codecs are excluded from the recommendation list with categorized counts in `excluded_by_reason`. Only standard RIFF/WAV PCM/float is accepted into this verified-header shortlist. **AIFF, FLAC, RF64, WAVE_FORMAT_EXTENSIBLE and other/compressed variants are excluded, not judged corrupt or converted**; the tool needs explicit parsers before supporting them. A successful header check is NOT waveform decoding, audible quality inspection, SFX semantic matching, video-action verification or a full cryptographic audio-content check. Candidate records include header-reported duration, format, sample rate, channels and bit depth, and still say `audio_content_decoded: false`, `audio_content_sha256_verified: false`, `selected: false`.

An optional user-supplied `steinberg-local-sample-rights-v1` registry remains **declaration only, not a checked legal grant**. Entries whose declared license ID includes `-NC` (noncommercial) or `-ND` (no derivatives) are conservatively excluded from this prospective commercial workflow pending separate rights clearance. Files without records can still appear **solely as unapproved review candidates**, labeled `no_rights_record`; other declarations are `user_declared_not_verified`. No candidate is marked `commercial_rights_cleared`; all are false, and nothing gets approved, selected, played, uploaded or placed in Cubase/Nuendo. The resulting report includes checked/excluded file counts and per-reason exclusions. This is a portable, file-only predevice slice, not live DAW acceptance.

## Slice 25 — explicit user approval and sample SHA-256 lock (pre-DAW only)

Two MCP tools in \`SteinbergFoleyApprovalTools.cs\` extend the local, review-only workflow. Neither places a clip in Cubase/Nuendo or exports a DAWproject.

**A. \`steinberg_foley_prepare_audio_review\`** receives an existing \`steinberg-foley-local-sample-candidates-v1\` draft and a NEW destination path. It checks that every event is unselected, contained in a bounded local library, not a redirected audio file/directory, and that the WAV header/size/duration still match the candidate preflight. It computes the actual file SHA-256 for each candidate (no audio copied or listened to). It creates a \`steinberg-foley-audio-review-sheet-v1\` JSON with candidate SHA-256, paths, audio technical metadata, unverified rights evidence, and all review flags false. The tool returns the **review sheet SHA-256** used in the next step.

**B. Human action**: Listen to the candidate; independently review the relevant intended commercial-use rights; ensure the sound fits the **unconfirmed** action; author a separate explicit approval JSON:
\`\`\`json
{
  "schema": "steinberg-foley-explicit-approval-v1",
  "review_sha256": "<sha256 returned by the prepare tool>",
  "approvals": [
    {
      "source_event_id": "mine_door_at_0",
      "relative_path": "Doors/wood_door_open.wav",
      "sample_sha256": "<candidate sample_sha256 in review sheet>",
      "listened_to_sample": true,
      "confirmed_matching_action": true,
      "reviewed_rights_for_intended_use": true,
      "approved_for_pre_daw_review_only": true,
      "reviewer_note": "Reviewer notes and independently checked licensing information"
    }
  ]
}
\`\`\`

**C. \`steinberg_foley_finalize_audio_review\`** takes the original review sheet, explicit approval file and a NEW destination. It rejects modified review sheets or draft source manifests, missing approval fields, duplicated event IDs, unknown candidate names/hashes, path traversal or symlinks, and mismatched audio SHA/size/header. It writes \`steinberg-foley-pre-daw-hashlocked-v1\` with SHA-256 fingerprints, the source event/video/contact hashes, audio header metadata and the separate approval-file hash. Unapproved events are counted rather than silently approved.

This **records a human attestation**, not independently verified legal rights, sound quality or precise motion onset. Audio bytes must be hashed again immediately before any later export/import. The final status is \`human_attested_samples_pending_daw_timing_review\`; \`action_timing_verified: false\`, \`daw_placement_approved: false\`, \`host_action: none\`. No media copied, DAW plugin called or live project altered. Actual Cubase/Nuendo Windows import is **NOT_RUN**.

## Slice 26 — Windows Cubase/Nuendo Foley preimport evidence gate (file only)

The MCP tool \`steinberg_foley_preimport_check\` consumes the **original unchanged** \`steinberg-foley-pre-daw-hashlocked-v1\` manifest, a separately human-authored frame/track review JSON, and a **new** JSON report destination. It is a portable, host-independent validator; it never launches Cubase/Nuendo, contacts the broker or inserts clips. The previous tool’s human attestation is preserved, **not** treated as proof of independent rights verification.

Human timing review format (example values must be replaced with actual hashes and paths):

\`\`\`json
{
  "schema": "steinberg-foley-human-timing-review-v1",
  "pre_daw_manifest_sha256": "<SHA-256 of exact pre-DAW manifest bytes>",
  "host_action": "none",
  "video": {
    "path": "C:\\Foley\\full-video.mp4",
    "sha256": "<SHA-256 of entire LOCAL source video>",
    "fps_numerator": 30000,
    "fps_denominator": 1001,
    "total_frames": 90000,
    "timecode_mode": "drop_frame"
  },
  "assignments": [
    {
      "source_event_id": "mine_door_at_0",
      "reviewed_window_sha256": "<SHA-256 of reviewed 2-second model window from pre-DAW manifest>",
      "sample_sha256": "<approved WAV content SHA-256 from pre-DAW manifest>",
      "event_frame": 1800,
      "duration_frames": 12,
      "track_name": "FOLEY_DOOR_OPEN",
      "human_confirmed_video_frame": true,
      "human_confirmed_track": true,
      "human_confirmed_source_window_to_video_mapping": true,
      "reviewer_note": "Manually checked 2-second model-window mapping and exact offset in full video"
    }
  ]
}
\`\`\`

The validator requires one unique frame/track assignment for **each** audio approved in the previous stage, no automatic-placement flags, matching model-window SHA, and fixed action-specific labels \`FOLEY_DOOR_OPEN\` / \`FOLEY_DOOR_CLOSE\`. The complete local video file **and each approved PCM/float WAV** must still match its recorded SHA-256; any changed byte, missing source, duplicate event, incorrect track, unsupported video format/timecode, negative/out-of-range frame or absent human confirmation fails closed without creating a report. Files are read but never copied or written back.

Timecode is derived from a zero-based **human-approved absolute frame index** using an explicitly declared rational FPS. Supported rates: 24, 25, 30, 50, 60 fps, plus 24000/1001, 30000/1001, 60000/1001. Non-drop labels use \`HH:MM:SS:FF\`; drop-frame labels (\`HH:MM:SS;FF\`) are supported **only** for 30000/1001 and 60000/1001. The report also includes numerical seconds from source-video start. Note that the **real video's FPS, frame count, native embedded timecode and action onset are NOT extracted or independently verified**: a SHA-256 match proves video-file identity only, while explicit human declarations govern the mapping from short, separately hashed model windows to the source timeline.

The generated \`steinberg-foley-preimport-acceptance-v1\` is **PASS_FILE_ONLY_READY_FOR_MANUAL_DAW_REVIEW**, not Windows host acceptance. It lists event frames, seconds, relative SMPTE-style timecodes, fixed track mapping, verified WAV and video hashes, and precise limitations. \`actual_cubase_nuendo_import=NOT_RUN\`, \`automatic_daw_import=false\`, \`host_action=none\`. The DAW tracks are expected names for later **manual track review**, not proof they exist in Cubase/Nuendo. Human sound/sync review and real Windows import remain outstanding. The \`main\` branch, GitHub Actions and the existing DAWproject exporter are not changed by this slice.

## Slice 27 — single-command Windows Foley acceptance entry (no DAW mutations)

New entry: \`scripts/verify-steinberg-windows-foley.ps1\`, designed for **Windows 11 PowerShell 5.1+** on the feature branch \`feat/steinberg-dualmode-adapters-20261104\`. It calls the already-existing \`verify-steinberg-offline.ps1\` subgate, rather than duplicating .NET tests. The project **does not require local Docker or GitHub Actions**.

From a clean checkout of the feature branch, with the .NET SDK pinned by \`global.json\` and locked NuGet packages already in the local cache:

\`\`\`powershell
.\scripts\verify-steinberg-windows-foley.ps1 -PreDawManifest "C:\Foley\pre-daw.json" -TimingReview "C:\Foley\timing-review.json"
\`\`\`

The two human-reviewed JSON files are mandatory for the **full file-only PASS**, and are **not generated** or fabricated by this command. The pre-DAW manifest comes from the two existing human approval MCP tools, with manually listened-to candidate WAV files; the timing review follows the JSON example in Slice 26 above (video/manifest SHA-256, human-confirmed full-video frame and track mapping). Without these two inputs, the script may still complete the offline test portion but ultimately returns \`BLOCKED_MISSING_HUMAN_REVIEW_INPUTS\` (nonzero exit). If the strict-offline restore finds no cached locked NuGet packages, it returns \`ENVIRONMENT_BLOCKED\`; it does **not** modify NuGet sources, use Docker or bypass any network restrictions.

The entrypoint rejects the wrong/detached Git branch, a dirty worktree, an unexpected Git SHA in the offline result, zero/failed tests, missing pinned official DAWproject XSD tests, missing/malformed/symlinked inputs, replaced source-video bytes or WAV bytes, invalid WAV RIFF PCM/float headers, unexpected sample paths or redirected parent folders, mismatched sample/window hashes, unsupported frame rates, invalid 29.97/59.94 drop-frame declarations, wrong \`FOLEY_DOOR_OPEN\`/\`FOLEY_DOOR_CLOSE\` track mapping, unapproved/duplicate/out-of-range event frames, and missing human timecode attestations.

Each invocation creates a **new GUID-named evidence directory** under \`%TEMP%\SteinbergFoleyWindowsAcceptance\`. Outputs are \`dotnet-offline/steinberg-offline-verification.json\`, a fresh TRX, and \`foley-windows-acceptance.json\` containing the branch + SHA, test count, per-file SHA checks, track/timecode rows and blockers. This launcher never creates an audio export, copies sample media, edits a project, contacts a DAW broker, installs a driver or automatically accepts sound.

Success is **\`PASS_FILE_ONLY_WINDOWS_PREFLIGHT\`**, explicitly **not** a Cubase/Nuendo import PASS. Even on native Windows, declared FPS/frame count, video action onset, 2-second model-window mapping, commercial rights and actual DAW track presence remain unverified independently. Actual DAW import is still \`NOT_RUN\` until a separate authorized Windows fixture/host acceptance, as described in \`docs/live-acceptance.md\`. No live fixture modification is authorized by this preflight.

The Render Linux build checks **seven Python source-level safety contracts** for this PowerShell entry plus existing .NET MCP tests; it cannot run Windows PowerShell or Cubase. Its JSON evidence explicitly says \`windows_powershell_runtime_execution: NOT_RUN\`. These source-level contracts are not a substitute for a successful native Windows run of this entrypoint.

## Slice 28 — one-command Windows failure diagnostics and evidence preservation

The Windows-only `scripts/verify-steinberg-windows-foley.ps1` remains read-only and creates a new GUID-named directory for each invocation; it now emits additional **structured failure evidence** without rerunning or requiring Cubase/Nuendo:

- `foley-windows-acceptance.json`: `phase`, `failure_code`, `error_type`, ordered `stage_history` with timestamps and PASS/BLOCKED states, PowerShell / Windows build metadata, operating system bitness, installed Git and .NET SDK version when available, and previous test/hash/timecode fields. The exact exception message describes the failing check.
- `dotnet-offline-output.log`: captured standard and error streams from the existing strict-offline .NET restore, build and MCP/XSD test gate. `dotnet-offline/steinberg-offline-verification.json` and a **fresh TRX** remain available when that gate reaches reporting. No existing user project is modified; no offline NuGet source is bypassed.
- `failure_code` identifies a machine-readable first blocker: `NOT_WINDOWS`, `SOURCE_BRANCH_OR_WORKTREE`, `DOTNET_RESTORE_BUILD_TEST_OR_XSD`, `HUMAN_APPROVAL_FILES_MISSING` or `MEDIA_HASH_OR_TIMECODE_OR_TRACK`. The `phase` field and `message` retain further diagnostic specificity. The launcher returns nonzero for every BLOCKED/FAIL status.
- `PASS_FILE_ONLY_WINDOWS_PREFLIGHT` is possible only after the unchanged local .NET gate, source verification and manually reviewed Foley manifest/timecode checks pass. Even then `cubase_nuendo_import=NOT_RUN`, `fps_metadata_probed=false` and `independent_license_review=false`.

The Render Linux CI-equivalent **is not a Windows runtime**. It checks 12 Python static source contracts for the new Windows diagnostic instrumentation, alongside the existing .NET MCP/XSD/CutPilot/X-CLIP checks. The Linux report explicitly retains `windows_powershell_runtime_execution=NOT_RUN`. Native Windows invocation, with its precise NuGet cache and human review inputs, must be separately proven; Windows and Cubase/Nuendo cannot be declared PASS on this cloud test.

## Slice 29 — native Windows PowerShell synthetic selftest before Foley review inputs

The existing one-command Windows entry `scripts/verify-steinberg-windows-foley.ps1` now accepts an **optional explicit `-SelfTest` switch** so the first Windows-native execution can be verified without a DAW, NuGet package cache, any real footage or human-approved Foley files. This is a narrow smoke test for script/runtime correctness and **does not** bypass or replace the full offline gate.

On the real Windows checkout, in the selected feature branch:

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\scripts\verify-steinberg-windows-foley.ps1 -SelfTest
```

The switch creates only a **synthetic one-second mono, 8 kHz, 16-bit PCM RIFF/WAV fixture** inside the script's existing **new GUID-named acceptance evidence directory**; it checks the real PowerShell `Read-WavHeader` implementation, `Must-File`, Windows SHA-256 output and changes to SHA-256 after mutating one audio byte without changing the RIFF header. It also tests `Rate-Nominal` and `Frame-TC` with independent known examples: 29.97 drop-frame at frames 1800 and 17982, 59.94 drop-frame at frame 3600, and 30fps non-drop at frame 1800. Audio is not played, and no actual WAV, video, project, sample library or user files are opened or changed.

The **only success status** for this explicit diagnostic mode is `PASS_WINDOWS_SCRIPT_SELFTEST_ONLY`, not `PASS_FILE_ONLY_WINDOWS_PREFLIGHT` or Cubase/Nuendo import acceptance. A native OS mismatch, broken script, invalid PCM header, unchanging SHA-256 after byte mutation, or incorrect SMPTE arithmetic fails closed with a nonzero exit and a structured `foley-windows-acceptance.json`. The report includes `selftest_requested`, `selftest_scope`, stage `WINDOWS_NATIVE_SCRIPT_SELFTEST`, a stable `WINDOWS_SELFTEST_FAILURE` blocker when relevant, and the same Windows PowerShell/OS evidence recorded by the main launcher. In this mode, `offline_gate=NOT_RUN`, `cubase_nuendo_import=NOT_RUN`, `fps_metadata_probed=false`; no change to `main` or GitHub Actions.

Without `-SelfTest`, the original full behavior **remains unchanged**: clean named Git branch, strict offline .NET/MCP/XSD tests, **two human-supplied approval/timing JSON inputs**, video and WAV SHA-256 recheck, human-confirmed frame and track mapping. Full PASS still requires all of those steps; no selftest result is accepted in their place.

The Render Linux job runs **17 source-level safety contracts** and the existing portable .NET tests; it **does not** prove native Windows execution. A `PASS_WINDOWS_SCRIPT_SELFTEST_ONLY` can only be claimed after an actual Windows run of this new switch and its evidence.

## Slice 30 — native OS attestation before Windows selftest

The user-editable `OS` environment variable is **not** sufficient evidence that a process runs on Windows. The `-SelfTest` and regular preflight both now require `System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` **and** `OS=Windows_NT`. This explicitly blocks a Linux PowerShell run with a spoofed environment variable from claiming `PASS_WINDOWS_SCRIPT_SELFTEST_ONLY`.

Render/Linux verifies **18 source-level safety contracts**, existing .NET/MCP/XSD regression tests and artifact integrity; it still **cannot run native Windows PowerShell or Cubase/Nuendo**. The next native milestone is to invoke `verify-steinberg-windows-foley.ps1 -SelfTest` from a normal Windows PowerShell session and inspect the newly generated `foley-windows-acceptance.json`. Only that on-device evidence can establish `PASS_WINDOWS_SCRIPT_SELFTEST_ONLY`; Cubase/Nuendo import remains `NOT_RUN` even then. No local Docker or GitHub Actions is required.

## Slice 31 — source SHA provenance shared by both Windows modes

The Windows launcher `verify-steinberg-windows-foley.ps1` now enforces **the same Git provenance check before either `-SelfTest` or the full preimport workflow**. It must run from a clean checkout of `feat/steinberg-dualmode-adapters-20261104`, at a valid 40-character commit SHA, and local `HEAD` must equal the checkout's local `refs/remotes/origin/feat/steinberg-dualmode-adapters-20261104` tracking ref. A dirty, detached, wrong-branch or untracked commit blocks evidence with nonzero exit. The report contains both `source_head` and `origin_feature_head`, making a native selftest attributable to a concrete source revision instead of an unidentified script.

This does **not** fetch or attest the live remote GitHub HEAD at test time; it checks the origin tracking ref available locally (a fresh clone is recommended). `-SelfTest` still avoids .NET restore, NuGet cache, user assets and Cubase/Nuendo, and only returns `PASS_WINDOWS_SCRIPT_SELFTEST_ONLY` after native execution. The Render/Linux gate checks **19 source-level contracts**, not actual Windows script execution. No change to `main`, DAW operation or GitHub Actions.

## Version and acceptance boundaries

- The **existing** broker/driver was validated on Cubase 15, not Cubase 12–14 or Nuendo. Do not advertise those versions as working until host compatibility and API limitations have been tested.
- These new adapters are independent of the MIDI Remote driver, so they can **produce portable files** before any host-specific integration is implemented. File creation does **not** establish correct timeline placement.
- Local Windows .NET build, test execution, external CLI execution, real Beat This! inference, Cubase/Nuendo import, and listening QA are **NOT_RUN** in this branch unless independently evidenced. This review confirmed source-level CLI argument compatibility, not live execution.
- GitHub Actions are not required. Existing `main` is unchanged.

## Slice-1 acceptance target

1. .NET solution compiles and the new MCP tool schemas are discoverable.
2. Missing provider returns `PROVIDER_NOT_CONFIGURED` without starting any process.
3. A local installed MIDIVOLT produces an SMF with an `MThd` header, with no overwrite.
4. An authorized GrooveMap checkpoint plus real stem produces `_tempo.mid`, `_drums.mid` and `_timing.json`; structural checks reject truncated MIDI and explicit stub source. Failure is explicit.
5. Any Cubase/Nuendo import and timing validation stays **NOT_RUN** until a real workstation test.

The next slice should use a typed cue/sample manifest and an explicit approval-before-import path; it must not assume MIDI Remote API 1.x can create or move arbitrary audio events on the DAW timeline.
