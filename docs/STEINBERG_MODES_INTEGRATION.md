# Steinberg dual-mode integration — slice 1 (2026-11-04)

This branch extends the existing **Cubase Complete MCP** server with optional **file-only** adapters. It does not embed, fork, merge, or modify the user's source projects. The upstream MIT license and the existing broker remain intact.

## Modes / precise scope

- **Arrangement (implemented in this slice):**
  - `steinberg_modes_status`: report local provider readiness and both modes.
  - `steinberg_arrange_tempo`: invoke GrooveMap Next V3 `groovemap-next suno --drums <wav> --out <prefix> --device cpu --checkpoint <trusted-local.ckpt>` to generate tempo/drum MIDI and timing JSON. A local checkpoint is mandatory, not auto-downloaded.
  - `steinberg_arrange_midi`: invoke MIDIVOLT's deterministic `midivolt-prompt --prompt <text> --out <file.mid>` to generate an independent, editable arrangement MIDI file.
  - **Not implemented:** sample-library search, placing WAV samples on the Cubase/Nuendo timeline, combining MIDI stems into a native project, automatic model installation, or changing a running DAW project.
- **Foley (declared, not implemented):** video spotting, sound-effect selection/generation, cue-to-timecode event manifests, and host import are later slices.

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
