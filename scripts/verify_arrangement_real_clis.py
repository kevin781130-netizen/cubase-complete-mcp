#!/usr/bin/env python3
"""Cloud-only real external provider CLI integration (no mocked MIDI provider).

MIDIVOLT's deterministic text sketch -> real SMF, GrooveMap Next's real
humanize-midi CLI -> actual drum-only SMF. Timing/groove is a deliberately
synthetic fixture because no approved Beat This! checkpoint is present.
"""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import subprocess
import sys

import mido
from groovemap_next.groove_humanizer import GrooveCell, GrooveTemplate, save_groove_template
from groovemap_next.models import BeatAnalysis
from groovemap_next.tempo_map import build_tempo_map

GROOVE_SHA = "3b9bc6d5a26212b35c92d91c1fd902061b187440"
MIDI_SHA = "0c23471b7508bc84383b7860fb976a0ab1e9cb73"

def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()

def run(argv: list[str], *, expect_success: bool = True) -> subprocess.CompletedProcess:
    p = subprocess.run(argv, capture_output=True, text=True, timeout=90, check=False)
    if expect_success and p.returncode != 0:
        raise RuntimeError(f"CLI_FAILED {Path(argv[0]).name} exit={p.returncode} {p.stderr[-800:]}")
    if not expect_success and p.returncode == 0:
        raise RuntimeError(f"CLI_NEGATIVE_UNEXPECTED_PASS {Path(argv[0]).name}")
    return p

def note_ons(path: Path, *, channel: int = 9) -> list[tuple[int,int]]:
    midi = mido.MidiFile(path)
    if midi.type not in (0,1) or midi.ticks_per_beat < 1 or not midi.tracks:
        raise RuntimeError("BAD_OR_EMPTY_STANDARD_MIDI_FILE")
    return [
        (int(msg.note),int(msg.velocity))
        for track in midi.tracks for msg in track
        if msg.type=="note_on" and msg.velocity>0 and getattr(msg,"channel",None)==channel
    ]

def verify(midi_exe: Path,groove_exe: Path,output_dir: Path) -> dict:
    if not midi_exe.is_file() or not groove_exe.is_file():
        raise RuntimeError("EXTERNAL_PROVIDER_EXECUTABLE_MISSING")
    if not output_dir.is_dir() or any(output_dir.iterdir()):
        raise RuntimeError("OUTPUT_DIRECTORY_MUST_BE_NEW_AND_EMPTY")
    prompt = "Full song, 8 bars in D minor at 110 bpm, chords melody bass drums"
    input_midi = output_dir/"midivolt-actual-prompt.mid"
    result = run([str(midi_exe),"--prompt",prompt,"--out",str(input_midi)])
    original = mido.MidiFile(input_midi)
    orig_note_count = sum(1 for tr in original.tracks for m in tr if m.type=="note_on" and m.velocity>0)
    drums=note_ons(input_midi)
    if len(drums)<4 or orig_note_count<=len(drums):
        raise RuntimeError(f"MIDIVOLT_MULTITRACK_OR_DRUMS_MISSING notes={orig_note_count} drums={len(drums)}")
    before=sha(input_midi)
    rejection=run([str(midi_exe),"--prompt",prompt,"--out",str(input_midi)],expect_success=False)
    if sha(input_midi)!=before:
        raise RuntimeError("MIDIVOLT_SOURCE_CHANGED_DURING_OVERWRITE_REJECTION")

    beat_times=tuple(i*0.5 for i in range(48))
    downbeats=tuple(i*2.0 for i in range(12))
    timeline=build_tempo_map(BeatAnalysis(
        beats_sec=beat_times,downbeats_sec=downbeats,
        source="synthetic-human-declared-not-beat-this"),repair_missing_beats=False)
    timing_path=output_dir/"synthetic-timing.json"
    groove_path=output_dir/"synthetic-groove.json"
    timing_path.write_text(json.dumps({
        "schema":"groovemap-next-timing-v2","ppq":timeline.ppq,
        "beats_per_bar":timeline.beats_per_bar,"origin_sec":timeline.origin_sec,
        "origin_beat_index":timeline.origin_beat_index,"tick_shift":0,
        "beats_sec":list(timeline.beats_sec),
        "downbeats_sec":list(timeline.downbeats_sec),
        "tempo_points":[{"beat_index":p.beat_index,"time_sec":p.time_sec,"bpm":p.bpm}
                       for p in timeline.tempo_points],
        "beat_source":"synthetic-human-declared-not-beat-this"
    },indent=2)+"\n",encoding="utf-8")
    save_groove_template(GrooveTemplate(
        subdivisions_per_beat=4,pattern_beats=4,
        cells=(GrooveCell(-1,"*",0.2,96,1),)),groove_path)
    output_midi=output_dir/"groovemap-actual-humanized-drums.mid"
    run([str(groove_exe),"humanize-midi","--midi",str(input_midi),
         "--timing",str(timing_path),"--groove",str(groove_path),
         "--out",str(output_midi)])
    after=note_ons(output_midi)
    if Counter(k for k,_ in after)!=Counter(k for k,_ in drums):
        raise RuntimeError("DRUM_NOTE_PITCH_MULTISET_CHANGED")
    if len(after)!=len(drums) or not any(v!=w for (_,v),(_,w) in zip(drums,after)):
        raise RuntimeError("GROOVEMAP_HUMANIZER_DID_NOT_TRANSFORM_GROOVE")
    if sha(input_midi)!=before:
        raise RuntimeError("GROOVEMAP_MODIFIED_MIDIVOLT_ORIGINAL")
    if not any(p.name.startswith("groovemap-actual") for p in output_dir.iterdir()):
        raise RuntimeError("GROOVEMAP_MISSING_OUTPUT")
    return {
        "status":"PASS_REAL_EXTERNAL_CLI_MIDIVOLT_TO_GROOVEMAP_HUMANIZE_ONLY",
        "groovemap_source_head":GROOVE_SHA,
        "midivolt_source_head":MIDI_SHA,
        "midivolt_prompt_engine":"REAL_DETERMINISTIC_NO_LLM",
        "midivolt_output_sha256":before,
        "midivolt_note_count":orig_note_count,
        "midivolt_drum_note_count":len(drums),
        "midivolt_original_unchanged":True,
        "midivolt_existing_output_rejected":True,
        "groovemap_real_humanize_midi_cli":True,
        "groovemap_output_sha256":sha(output_midi),
        "groovemap_output_drum_note_count":len(after),
        "groovemap_groove_transformation_observed":True,
        "groovemap_timing_source":"SYNTHETIC_TEST_FIXTURE_NOT_BEAT_THIS",
        "groovemap_real_beat_this_inference":"NOT_RUN_NO_APPROVED_CHECKPOINT",
        "groovemap_actual_suno_wav_tempo_outputs":"NOT_RUN_NO_APPROVED_CHECKPOINT",
        "full_multitrack_result":"MIDIVOLT_ORIGINAL_PLUS_SEPARATE_DRUM_ONLY_HUMANIZED_MIDI",
        "cubase_nuendo_import":"NOT_RUN",
        "github_actions":"NOT_USED"
    }

def main() -> int:
    p=argparse.ArgumentParser()
    p.add_argument("--midivolt-exe",required=True)
    p.add_argument("--groovemap-exe",required=True)
    p.add_argument("--output-dir",required=True)
    a=p.parse_args()
    try:
        result=verify(Path(a.midivolt_exe),Path(a.groovemap_exe),Path(a.output_dir))
    except (RuntimeError,OSError,ValueError,subprocess.SubprocessError,KeyError,
            json.JSONDecodeError) as exc:
        print("ARRANGEMENT_REAL_CLI_FAILED "+str(exc),file=sys.stderr)
        return 2
    evidence=Path(a.output_dir)/"report.json"
    evidence.write_text(json.dumps(result,indent=2,sort_keys=True)+"\n",encoding="utf-8")
    print("STEINBERG_ARRANGEMENT_REAL_CLI_PASS "+json.dumps(result,sort_keys=True))
    return 0

if __name__=="__main__":
    sys.exit(main())
