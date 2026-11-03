#!/usr/bin/env python3
"""Opt-in, read-only CutPilot scene-cut spotting provider.

Requires separately installed CutPilot's cutpilot/scenes.py and OpenCV, plus
an explicit ffprobe binary. Stdout is ONE JSON payload consumed by MCP.
Visual scene boundaries are NOT semantic actions or audible SFX detections.
"""
from __future__ import annotations

import argparse
from fractions import Fraction
import importlib.util
import json
import math
from pathlib import Path
import subprocess
import sys


def probe(ffprobe: Path, video: Path) -> tuple[int, int, int]:
    command = [
        str(ffprobe), "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=avg_frame_rate,r_frame_rate,nb_frames:format=duration",
        "-of", "json", str(video),
    ]
    result = subprocess.run(command, capture_output=True, text=True,
                            check=True, timeout=30)
    data = json.loads(result.stdout)
    streams = data.get("streams", [])
    if not streams:
        raise ValueError("No video stream found by ffprobe.")
    item = streams[0]
    rate = Fraction(str(item.get("avg_frame_rate", "0/1")))
    nominal = Fraction(str(item.get("r_frame_rate", "0/1")))
    if rate <= 0 or rate > 240 or nominal <= 0:
        raise ValueError("Missing or unsupported rational frame rate.")
    if abs(float(rate - nominal)) > 0.02:
        raise ValueError("Possible variable-frame-rate video: not safe to frame-align. Transcode to CFR first.")
    frames_text = str(item.get("nb_frames", ""))
    if not frames_text.isdigit():
        raise ValueError("ffprobe must report nb_frames; no frame count guessing.")
    count = int(frames_text)
    if count < 1 or count > 20_000_000 or rate.numerator > 120000 or rate.denominator > 1001:
        raise ValueError("Frame rate or frame count out of supported range.")
    if count * rate.denominator / rate.numerator > 600:
        raise ValueError("Only the first ten minutes per video are supported in this bounded provider.")
    return rate.numerator, rate.denominator, count


def load_cutpilot(root: Path):
    scene_module = root / "cutpilot" / "scenes.py"
    if not scene_module.is_file() or scene_module.is_symlink():
        raise ValueError("CutPilot cutpilot/scenes.py is missing or linked.")
    spec = importlib.util.spec_from_file_location("cutpilot_scene_provider_local", scene_module)
    if spec is None or spec.loader is None:
        raise ValueError("Unable to load the separately installed CutPilot scene detector.")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if not callable(getattr(module, "detect_scene_cuts", None)):
        raise ValueError("CutPilot does not expose detect_scene_cuts.")
    return module.detect_scene_cuts


def build_payload(video: Path, root: Path, ffprobe: Path, interval: float, threshold: float) -> dict:
    if not video.is_file() or video.is_symlink():
        raise ValueError("Expected an existing, non-symlink local video.")
    if video.suffix.lower() not in {".mp4", ".mov", ".mkv", ".mxf"}:
        raise ValueError("Unsupported video extension.")
    num, den, count = probe(ffprobe, video)
    detect = load_cutpilot(root)
    end = (count - 1) * den / num
    cuts = detect(video, 0.0, end, sample_interval=interval,
                  threshold=threshold, min_gap=0.65)
    if not isinstance(cuts, list) or len(cuts) > 128:
        raise ValueError("Scene detector must return a list of at most 128 cuts.")
    cues = []
    seen = set()
    for seconds in cuts:
        if type(seconds) not in (int, float) or not math.isfinite(seconds):
            raise ValueError("CutPilot returned a non-finite scene timestamp.")
        frame = round(seconds * num / den)
        if frame <= 0 or frame >= count:
            raise ValueError("Scene timestamp is outside the video frame range.")
        if frame in seen:
            continue
        seen.add(frame)
        cues.append({
            "frame": frame,
            "duration_frames": 0,
            "event_label": "Visual scene boundary - review Foley",
            "track": "other",
            "sfx_query": "review whether a scene transition sound is needed",
        })
    cues.sort(key=lambda x: x["frame"])
    stat = video.stat()
    return {
        "provider": "cutpilot.scenes.detect_scene_cuts",
        "detection_type": "visual_histogram_scene_boundary_only",
        "video_path": str(video),
        "video_size_bytes": stat.st_size,
        "video_mtime_ns": stat.st_mtime_ns,
        "fps_numerator": num,
        "fps_denominator": den,
        "video_frame_count": count,
        "cues": cues,
        "source_timing": "ffprobe_avg_frame_rate_and_nb_frames_assumed_constant",
        "cut_precision": "sample_interval_seconds_not_frame_accurate",
        "human_review_required": True,
        "automatic_action_recognition": False,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--video", required=True)
    parser.add_argument("--cutpilot-root", required=True)
    parser.add_argument("--ffprobe", required=True)
    parser.add_argument("--interval", type=float, default=0.25)
    parser.add_argument("--threshold", type=float, default=0.52)
    args = parser.parse_args()
    if not 0.10 <= args.interval <= 1.0:
        raise ValueError("Sampling interval must be 0.10 to 1.0 seconds.")
    if not 0.20 <= args.threshold <= 1.50:
        raise ValueError("Histogram distance threshold must be 0.20 to 1.50.")
    video = Path(args.video).resolve(strict=True)
    root = Path(args.cutpilot_root).resolve(strict=True)
    ffprobe = Path(args.ffprobe).resolve(strict=True)
    if not ffprobe.is_file() or ffprobe.is_symlink():
        raise ValueError("ffprobe binary must be an actual file.")
    result = build_payload(video, root, ffprobe, args.interval, args.threshold)
    json.dump(result, sys.stdout, ensure_ascii=False, separators=(",", ":"))
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, OSError, subprocess.SubprocessError, json.JSONDecodeError) as exc:
        print("CUTPILOT_SCENE_PROVIDER_REJECTED: " + str(exc), file=sys.stderr)
        raise SystemExit(2)
