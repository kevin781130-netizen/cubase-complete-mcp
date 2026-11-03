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
