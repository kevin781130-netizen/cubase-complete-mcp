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
