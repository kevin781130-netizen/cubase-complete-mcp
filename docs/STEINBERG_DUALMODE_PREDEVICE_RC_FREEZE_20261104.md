# Steinberg dual-mode MCP — pre-device RC freeze (2026-11-04)

**Release classification: PREDEVICE_RC_PORTABLE_VERIFIED — NOT a Windows/DAW or production acceptance.**

This is a release-candidate *source snapshot* for the owner's optional arrangement and Foley file-only extensions. It does not change or merge `main`, install anything on a workstation, use GitHub Actions, or modify a running DAW session.

## Frozen version and evidence binding

- Source repository: `kevin781130-netizen/cubase-complete-mcp`.
- Development branch: `feat/steinberg-dualmode-adapters-20261104`.
- **Freeze ref:** `rc/steinberg-dualmode-predevice-20261104` — branch created **from the exact source commit** that passes the final Render gate. Treat the immutable **commit SHA**, not the movable ref name, as the frozen identity. The GitHub branch is a convenience pointer, not a protected tag or branch-protection guarantee.
- Cloud verifier: [portable Render report](https://steinberg-dualmode-dotnet-portable.onrender.com/report.json), `source_head`, `status` and `rc_nondevice_dotnet_suites`. Every claim is SHA-scoped: if the report's `source_head` and the frozen branch HEAD differ, the current report **cannot** certify that branch. No stale reports may be substituted.
- Latest tested implementation before this documentation-only closeout: `865351451f9baeaa9d7eecc352d840dafd4d9056`. The final RC commit is the one actually checked out and independently reported by Render **after** this closeout document is added.
- Existing detailed engineering evidence and limitations: [pre-device closeout](STEINBERG_PREDEVICE_CLOSEOUT_20261104.md).
- SHA-pinned external sources: GrooveMap `3b9bc6d5a26212b35c92d91c1fd902061b187440`; MIDIVOLT `0c23471b7508bc84383b7860fb976a0ab1e9cb73`; CutPilot original scene-detector Git blob `2c4fd2e9a931358f3dc2420b82c63c07d2807ce5`; Beat This! final0 checkpoint SHA-256 `8c328b45f59d8dd3dff219253ff6a8d6482be57d0133a29140e2febbf8eb8331`.
- Runtime: Render Linux native Python **3.13.11**, .NET SDK **8.0.301**, model CPU-only. These are **cloud acceptance runtimes**, not the Windows host's proven configuration.

## Audited portable PASS (when final source SHA matches)

| Gate | Actual accepted scope |
|---|---|
| .NET solution restore and build | **PASS**, pinned NuGet lock and SDK, no compiler errors or warnings. Building the Windows target on Linux **does not** run WindowsDesktop testhost or Cubase. |
| MCP Server complete Linux-executable non-device test baseline | **64/64 PASS**, excluding two real external-provider integration cases executed separately with required dependencies. TRX is parsed fail-closed. |
| Historical Steinberg-specific unit filter | **58/58 PASS** within the server project, **not** the entire repository. Official `Project.xsd` and `MetaData.xsd` schema tests included. |
| Real arrangement external-provider process | `SteinbergModesTools.ArrangeMidi` invokes installed MIDIVOLT CLI **1/1 PASS**, with overwrite refusal; real GrooveMap MIDI Humanize consumes its MIDI and preserves drum notes. |
| Real Tempo pipeline through MCP | `SteinbergModesTools.ArrangeTempo` invokes real GrooveMap and SHA-verified Beat This! final0 **1/1 PASS**, generating validated Tempo MIDI, Drum MIDI and timing JSON from **synthetic** 24-second WAV. |
| Foley DAWproject file-only | Synthetic approval → frame assignment → independent ZIP/WAV SHA/XSD readback **PASS**; **13/13** negative/mutation checks. No assertion that synthetic flags represent actual human review. |
| CutPilot real OpenCV and FFprobe | **PASS** on an actual encoded/decoded, synthetic 90-frame MP4 with scene cuts at 1.25s and 2.50s. Real camera footage and event-onset accuracy not measured. |
| Windows script source-level gate | **19/19 PASS** static/source-only, **not** Windows PowerShell execution. |

## Explicitly NOT_RUN / NOT_VERIFIED

1. **Core/Broker/CLI complete test project — `NOT_RUN_WINDOWS_DESKTOP_RUNTIME_REQUIRED`.** Direct Linux testhost attempt genuinely failed because `Microsoft.WindowsDesktop.App 8.0` is unavailable, even though solution build passed. This is an OS/runtime boundary, **not** a passing test suite; do not install a spoofed framework or silently skip the result. Must run the project with the native Windows desktop runtime.
2. **Native Windows PowerShell preflight/self-test and Cubase/Nuendo broker/driver**, incl. installer receipt, disposable project ownership and non-destructive commands: `NOT_RUN`.
3. **Actual Cubase/Nuendo import**, audio clip alignment, file playback, MIDI tempo tracks, save/reopen, drop-frame/project clock behavior and DAW host safety: `NOT_RUN`.
4. **Commercial/real input acceptance**: real authorized SUNO drum stem or full song with independently measured beat/downbeat precision, real video Foley onset/location accuracy, human WAV listening/rights review, and model performance across representative content: `NOT_VERIFIED`.
5. Dedicated **X-CLIP/SigLIP genuine-weight model** testing occurred on a **separate Render service and earlier SHA**, not this candidate's portable gate; production accuracy and real Foley event placement remain unverified.
6. **Cubase 12–14 and Nuendo version support** have not been established by the offline gate; do not infer host compatibility from XSD schema or Linux build.

## Change and release policy

- **Freeze** this RC snapshot by retaining its complete commit SHA and leaving `rc/steinberg-dualmode-predevice-20261104` unchanged after creation. Future code changes belong in a separate feature/RC branch and require fresh verification. This commit-ref convention does **not** technically enforce GitHub branch protection.
- **No merge to `main`** and no production release declaration before device and human gates pass.
- No need to download all developer projects on the user's workstation. Install only the particular external provider required for the host test, using an approved local setup.
- **First Windows step**: read `scripts/verify-steinberg-windows-foley.ps1` and execute its `-SelfTest` on the exact RC SHA. A self-test PASS only proves the PowerShell self-test; it is not Cubase acceptance.
- After Windows testhost/PowerShell acceptance, proceed to a **disposable Cubase 15.0.30+** test project and separately authorized human-owned media. Keep Windows and real-input results separate from this Linux report.

**Success criterion for this freeze:** final RC branch HEAD equals `report.json.source_head` and Render has `LIVE` for that SHA, with `PASS_LINUX_PORTABLE_ONLY`; its `Core/Broker/CLI` result must explicitly be `NOT_RUN_WINDOWS_DESKTOP_RUNTIME_REQUIRED`, never a false `PASS`.
