#!/usr/bin/env python3
"""Real MIT Beat This! final0 checkpoint + authentic GrooveMap Next WAV→MIDI gate.

No mocked tempo model, manually declared beats, fake checkpoints or real
commercial tracks. Uses synthesized PCM WAV with 120-BPM kick/snare/hat audio.
Outputs are marked SYNTHETIC_INPUT. Never claims music accuracy or DAW import.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import sys
import time
import wave

import mido
import numpy as np
from groovemap_next.pipeline import run_suno_drum_pipeline

FINAL0_SHA256 = "8c328b45f59d8dd3dff219253ff6a8d6482be57d0133a29140e2febbf8eb8331"
GROOVEMAP_SHA = "3b9bc6d5a26212b35c92d91c1fd902061b187440"
MIN_SIZE = 72 * 1024 * 1024
MAX_SIZE = 90 * 1024 * 1024


def digest(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def synth_wav(path: Path) -> dict:
    sample_rate = 22050
    duration_s = 24
    pcm = np.zeros(sample_rate * duration_s, dtype=np.float32)
    rng = np.random.default_rng(seed=20261104)
    for eighth in range(48):
        on = 0.5 * eighth
        start = round(on * sample_rate)
        count = round(sample_rate * 0.14)
        times = np.arange(count, dtype=np.float32) / sample_rate
        # Tonal kick on downbeat, noise snare on backbeat, short hi-hat
        kick = (0.64 * np.sin(2 * math.pi * (84 - 37 * times) * times)
                * np.exp(-32 * times))
        snare = rng.standard_normal(count).astype(np.float32) * np.exp(-29 * times) * 0.18
        highhat = rng.standard_normal(count).astype(np.float32) * np.exp(-140 * times) * 0.09
        block = kick if eighth % 4 in (0, 2) else snare
        pcm[start:start + count] += block + highhat
    # Prevent edge clipping and preserve source rights: computer-synthesized only.
    normalized = np.clip(pcm, -0.98, 0.98)
    with wave.open(str(path), "wb") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(sample_rate)
        f.writeframes((normalized * 32767).astype("<i2").tobytes())
    return {"sample_rate": sample_rate, "duration_seconds": duration_s,
            "nominal_synthetic_bpm": 120, "sha256": digest(path)}


def verify(root: Path, checkpoint: Path) -> dict:
    if not root.is_dir() or list(root.iterdir()):
        raise RuntimeError("INPUT_OUTPUT_DIRECTORY_NOT_NEW_AND_EMPTY")
    if not checkpoint.is_file() or checkpoint.is_symlink():
        raise RuntimeError("CHECKPOINT_MISSING_OR_UNSAFE")
    if not (MIN_SIZE <= checkpoint.stat().st_size <= MAX_SIZE):
        raise RuntimeError("FINAL0_CHECKPOINT_SIZE_INVALID")
    if digest(checkpoint) != FINAL0_SHA256:
        raise RuntimeError("FINAL0_CHECKPOINT_SHA256_MISMATCH")

    import importlib.metadata
    from beat_this.inference import Audio2Beats
    import torch

    # Real checkpoint must be loaded in CPU mode, with auto-download disabled
    # because the explicit local pathname is verified before model import.
    source = root/"synthesized-120bpm-drum-stem.wav"
    context = synth_wav(source)
    prefix = root/"real-model"
    t0 = time.monotonic()
    out = run_suno_drum_pipeline(
        source, prefix, device="cpu", checkpoint=str(checkpoint),
        humanize_source=True, pattern_bars=1
    )
    elapsed = round(time.monotonic()-t0,3)
    required = [out.tempo_midi,out.drum_midi,out.timing_json,
                out.humanized_midi,out.groove_json]
    if not all(f and f.is_file() and f.stat().st_size>30 for f in required):
        raise RuntimeError("GROOVEMAP_MODEL_REQUIRED_OUTPUT_MISSING")

    timing = json.loads(out.timing_json.read_text(encoding="utf-8"))
    beats = timing.get("beats_sec")
    points = timing.get("tempo_points")
    if (timing.get("schema")!="groovemap-next-timing-v2"
        or timing.get("beat_source")!="CPJKU/beat_this:minimal"
        or timing.get("transcription_source")!="cleanroom-dsp-3class"
        or not isinstance(beats,list) or len(beats)<5
        or not isinstance(points,list) or len(points)<4):
        raise RuntimeError("BEAT_THIS_REAL_MODEL_TIMING_PROVENANCE_INVALID")
    if not all(math.isfinite(float(x)) and 0<=float(x)<=context["duration_seconds"]+2
               for x in beats):
        raise RuntimeError("INVALID_BEAT_TIME")
    if not all(float(b)>float(a) for a,b in zip(beats,beats[1:])):
        raise RuntimeError("BEAT_TIMES_UNSORTED_OR_DUPLICATE")
    if not all(20<=float(item["bpm"])<=400 for item in points):
        raise RuntimeError("INVALID_TEMPO_BOUNDARIES")

    tempo = mido.MidiFile(out.tempo_midi)
    drums = mido.MidiFile(out.drum_midi)
    tempo_events = sum(1 for track in tempo.tracks for event in track if event.type=="set_tempo")
    drum_notes = [msg for track in drums.tracks for msg in track
                  if msg.type=="note_on" and msg.velocity>0 and getattr(msg,"channel",None)==9]
    if tempo_events < 4 or not drum_notes:
        raise RuntimeError("REAL_MODEL_MIDI_TEMPO_OR_DRUM_EVENTS_EMPTY")
    if digest(source)!=context["sha256"]:
        raise RuntimeError("MODEL_MODIFIED_SOURCE_WAV")

    result={
        "status":"PASS_REAL_BEAT_THIS_FINAL0_GROOVEMAP_SYNTHETIC_WAV_TO_TEMPO_MIDI_ONLY",
        "real_model":True,
        "model":"CPJKU/beat_this final0 pretrained",
        "model_license":"MIT_WITH_TRAINING_DATA_LEGAL_CAVEAT",
        "model_sha256":FINAL0_SHA256,
        "model_checkpoint_bytes":checkpoint.stat().st_size,
        "torch_version":torch.__version__,
        "beat_this_version":importlib.metadata.version("beat-this"),
        "groovemap_source_head":GROOVEMAP_SHA,
        "input_audio":"LOCALLY_SYNTHESIZED_DRUM_STEM_NOT_SUNO",
        "input":context,
        "source_audio_unchanged":True,
        "inference_cpu_only":True,
        "inference_elapsed_seconds":elapsed,
        "timing_schema":timing["schema"],
        "beat_source":timing["beat_source"],
        "beat_count":len(beats),
        "tempo_points":len(points),
        "tempo_midi_events":tempo_events,
        "drum_note_events":len(drum_notes),
        "tempo_midi_sha256":digest(out.tempo_midi),
        "drum_midi_sha256":digest(out.drum_midi),
        "timing_json_sha256":digest(out.timing_json),
        "humanized_midi_sha256":digest(out.humanized_midi),
        "groove_json_sha256":digest(out.groove_json),
        "real_world_suno_alignment":"NOT_VERIFIED",
        "commercial_rights_of_training_corpus":"LEGAL_REVIEW_RECOMMENDED",
        "cubase_nuendo_windows_host":"NOT_RUN",
        "github_actions":"NOT_USED"
    }
    return result


def main() -> int:
    ap=argparse.ArgumentParser()
    ap.add_argument("--checkpoint",required=True)
    ap.add_argument("--output-dir",required=True)
    a=ap.parse_args()
    try:
        out=Path(a.output_dir)
        data=verify(out,Path(a.checkpoint))
        (out/"report.json").write_text(json.dumps(data,indent=2,sort_keys=True)+"\n",encoding="utf-8")
        print("STEINBERG_REAL_BEAT_THIS_FINAL0_PASS "+json.dumps(data,sort_keys=True),flush=True)
        return 0
    except (RuntimeError,OSError,ValueError,TypeError,KeyError,ImportError) as exc:
        print("STEINBERG_REAL_BEAT_THIS_FINAL0_FAIL "+repr(exc),file=sys.stderr,flush=True)
        return 2


if __name__=="__main__":
    sys.exit(main())
