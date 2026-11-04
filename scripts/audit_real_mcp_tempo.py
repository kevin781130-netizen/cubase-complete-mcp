#!/usr/bin/env python3
"""Independent readback of the genuine Steinberg ArrangeTempo MCP outputs.

This is NOT a second model inference and does not claim real-song accuracy.
It audits real files emitted by the C# MCP -> GrooveMap CLI -> Beat This!
checkpoint during the separate opt-in .NET TRX test.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import sys

import mido

EXPECTED_MODEL_SHA = "8c328b45f59d8dd3dff219253ff6a8d6482be57d0133a29140e2febbf8eb8331"


def require(predicate, code):
    if not predicate:
        raise ValueError(code)


def hash_file(path: Path) -> str:
    with path.open("rb") as handle:
        sha = hashlib.sha256()
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            sha.update(chunk)
        return sha.hexdigest()


def verify(base: Path, wav: Path, independent_report: Path) -> dict:
    require(base.is_dir() and not base.is_symlink(), "MCP_TEMPO_OUTPUT_FOLDER_INVALID")
    require(wav.is_file() and not wav.is_symlink(), "SOURCE_WAV_MISSING")
    prior = json.loads(independent_report.read_text(encoding="utf-8"))
    require(prior.get("status") ==
            "PASS_REAL_BEAT_THIS_FINAL0_GROOVEMAP_SYNTHETIC_WAV_TO_TEMPO_MIDI_ONLY",
            "NO_INDEPENDENT_REAL_MODEL_PASS")
    require(prior.get("model_sha256") == EXPECTED_MODEL_SHA and
            prior.get("real_model") is True and
            prior.get("input", {}).get("sha256") == hash_file(wav),
            "SOURCE_OR_MODEL_IDENTITY_MISMATCH")
    prefix = base / "mcp-arrange-real"
    tempo_path = Path(str(prefix) + "_tempo.mid")
    drums_path = Path(str(prefix) + "_drums.mid")
    timing_path = Path(str(prefix) + "_timing.json")
    for path in (tempo_path, drums_path, timing_path):
        require(path.is_file() and not path.is_symlink() and
                24 < path.stat().st_size < 100_000_000,
                "MCP_OUTPUT_MISSING_OR_UNSAFE")
    require(not Path(str(tempo_path) + ".mcp-inflight").exists(),
            "STALE_MCP_LOCK")
    timing = json.loads(timing_path.read_text(encoding="utf-8"))
    beats = timing.get("beats_sec")
    points = timing.get("tempo_points")
    require(
        timing.get("schema") == "groovemap-next-timing-v2" and
        timing.get("beat_source") == "CPJKU/beat_this:minimal" and
        timing.get("transcription_source") == "cleanroom-dsp-3class" and
        isinstance(beats, list) and len(beats) >= 5 and
        isinstance(points, list) and len(points) >= 4,
        "MCP_TIMING_PROVENANCE_OR_COUNTS_INVALID")
    require(all(isinstance(b, (float, int)) and math.isfinite(b)
                and 0 <= b <= 28 for b in beats) and
            all(b > a for a, b in zip(beats, beats[1:])),
            "MCP_BEAT_TIMESTAMPS_INVALID")
    require(all(20 <= p.get("bpm", 0) <= 400 for p in points),
            "MCP_BPM_VALUES_INVALID")
    tempo = mido.MidiFile(tempo_path)
    drums = mido.MidiFile(drums_path)
    require(tempo.type in (0, 1) and drums.type in (0, 1),
            "INVALID_MIDI_TYPE")
    tempo_events = sum(
        msg.type == "set_tempo" for track in tempo.tracks for msg in track)
    drum_notes = [msg for track in drums.tracks for msg in track
                  if msg.type == "note_on" and msg.velocity > 0
                  and getattr(msg, "channel", -1) == 9]
    require(tempo_events >= 4 and len(drum_notes) >= 1,
            "TEMPO_OR_DRUM_MIDI_EVENTS_ABSENT")
    require(hash_file(wav) == prior["input"]["sha256"],
            "SOURCE_WAV_MUTATED_DURING_MCP")
    return {
        "status": "PASS_REAL_STEINBERG_MCP_TO_GROOVEMAP_BEAT_THIS_FINAL0_SYNTHETIC_WAV",
        "source": "SteinbergModesTools.ArrangeTempo real child-process execution",
        "input_audio": "SYNTHETIC_DRUM_WAV_NOT_REAL_SUNO",
        "input_audio_sha256": hash_file(wav),
        "verified_model_sha256": EXPECTED_MODEL_SHA,
        "mcp_timing_schema": timing["schema"],
        "mcp_beat_source": timing["beat_source"],
        "beats_detected": len(beats),
        "tempo_points": len(points),
        "tempo_meta_events": tempo_events,
        "drum_midi_note_ons": len(drum_notes),
        "tempo_midi_sha256": hash_file(tempo_path),
        "drum_midi_sha256": hash_file(drums_path),
        "timing_json_sha256": hash_file(timing_path),
        "original_wav_unchanged": True,
        "existing_outputs_unchanged_after_rejected_reuse":
            "ASSERTED_BY_REAL_DOTNET_TEST",
        "cubase_nuendo_import": "NOT_RUN",
        "real_suno_alignment": "NOT_VERIFIED",
        "windows_host": "NOT_RUN",
    }


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--output-dir", type=Path, required=True)
    p.add_argument("--wav", type=Path, required=True)
    p.add_argument("--independent-report", type=Path, required=True)
    a = p.parse_args()
    try:
        result = verify(a.output_dir, a.wav, a.independent_report)
        (a.output_dir / "report.json").write_text(
            json.dumps(result, indent=2, sort_keys=True) + "\n",
            encoding="utf-8")
        print("STEINBERG_REAL_MCP_TEMPO_AUDIT_PASS " +
              json.dumps(result, sort_keys=True), flush=True)
        return 0
    except (ValueError, OSError, KeyError, TypeError, json.JSONDecodeError,
            EOFError, mido.MidiFileError) as exc:
        print("STEINBERG_REAL_MCP_TEMPO_AUDIT_FAIL " + repr(exc),
              file=sys.stderr, flush=True)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
