#!/usr/bin/env python3
"""Optional local X-CLIP video-to-action *candidate* provider.

Model weights, Python packages and video remain local. No model download.
Clip-level text similarities are not calibrated probabilities or event boundaries.
"""
from __future__ import annotations

import argparse
from fractions import Fraction
import json
import math
from pathlib import Path
import subprocess
import sys

# Closed vocabulary: no automatic SFX matching and no open-ended hallucinations.
# The final entries are abstention classes; they do not create sound cues.
ACTIONS = (
    ("walking", "footsteps", "a person walking on the ground", "review possible footsteps and surface"),
    ("running", "footsteps", "a person running on the ground", "review possible running footsteps"),
    ("opening_door", "doors", "a person opens a door", "review door opening and handle sound"),
    ("closing_door", "doors", "a person closes a door", "review door latch or close sound"),
    ("object_impact", "impacts", "an object collides with or hits another object", "review possible object impact"),
    ("clapping", "impacts", "a person claps their hands", "review possible hand clap"),
    ("driving", "vehicles", "a car drives or passes by", "review possible vehicle engine or tire sound"),
    ("handling_cloth", "cloth", "a person moves or handles fabric and clothing", "review possible fabric movement"),
    ("idle_speech", "other", "a person sitting and speaking without visible physical action", ""),
    ("quiet_scene", "other", "a quiet scene with no obvious physical activity", ""),
)
PROMPTS = [row[2] for row in ACTIONS]
MAX_WINDOWS = 12
FRAMES_PER_WINDOW = 8


def probe(ffprobe: Path, video: Path) -> tuple[int, int, int]:
    command = [str(ffprobe), "-v", "error", "-select_streams", "v:0",
               "-show_entries", "stream=avg_frame_rate,r_frame_rate,nb_frames",
               "-of", "json", str(video)]
    result = subprocess.run(command, capture_output=True, text=True,
                            check=True, timeout=30)
    data = json.loads(result.stdout)
    streams = data.get("streams") or []
    if len(streams) != 1:
        raise ValueError("ffprobe must find exactly one primary video stream.")
    source = streams[0]
    rate = Fraction(str(source.get("avg_frame_rate", "0/1")))
    nominal = Fraction(str(source.get("r_frame_rate", "0/1")))
    if rate <= 0 or rate > 240 or nominal <= 0 or abs(float(rate - nominal)) > 0.02:
        raise ValueError("Invalid frame rate or possible VFR; explicit CFR conversion required.")
    count_text = str(source.get("nb_frames", ""))
    if not count_text.isdigit():
        raise ValueError("Exact nb_frames is required; no frame-count estimation.")
    count = int(count_text)
    if count < 8 or count > 20_000_000 or rate.numerator > 120000 or rate.denominator > 1001:
        raise ValueError("Unsupported video frame count / rate.")
    if count * rate.denominator / rate.numerator > 30.0:
        raise ValueError("Local X-CLIP first slice supports at most 30 seconds of video.")
    return rate.numerator, rate.denominator, count


def window_grid(frame_count: int, numerator: int, denominator: int):
    duration = frame_count * denominator / numerator
    windows = min(MAX_WINDOWS, max(1, round(duration / 2.0)))
    return [
        (round(i * frame_count / windows),
         round((i + 1) * frame_count / windows))
        for i in range(windows)
    ]


def frame_indices(start: int, end: int) -> list[int]:
    if end - start < FRAMES_PER_WINDOW:
        raise ValueError("Video clip has too few frames for 8-frame X-CLIP classification.")
    return [start + round(i * (end - start - 1) / (FRAMES_PER_WINDOW - 1))
            for i in range(FRAMES_PER_WINDOW)]


def decide(scores, start: int, end: int, min_score: float, min_margin: float):
    if len(scores) != len(ACTIONS) or any(not math.isfinite(float(x)) or
                                           float(x) < 0 or float(x) > 1 for x in scores):
        raise ValueError("Model similarity scores are invalid.")
    ranked = sorted(range(len(scores)), key=lambda i: float(scores[i]), reverse=True)
    top, runner = ranked[:2]
    score, margin = float(scores[top]), float(scores[top]) - float(scores[runner])
    label, track, _, query = ACTIONS[top]
    if track == "other" or score < min_score or margin < min_margin:
        return None
    frame = (start + end - 1) // 2  # coarse center-of-window ONLY
    return {
        "frame": frame,
        "duration_frames": 0,
        "event_label": "Possible action: " + label.replace("_", " "),
        "track": track,
        "sfx_query": query,
        "action_class": label,
        "relative_score": round(score, 6),
        "score_margin": round(margin, 6),
        "window_start_frame": start,
        "window_end_frame": end,
    }


def classify(video: Path, model_dir: Path, num: int, den: int, count: int,
             min_score: float, min_margin: float, traces=None, runtime=None) -> tuple[list[dict], int]:
    # Heavy dependencies deliberately loaded only when real inference was requested.
    import cv2
    import torch
    from transformers import XCLIPModel, XCLIPProcessor

    # Model must already be downloaded by the user. No arbitrary remote Python code.
    if runtime is None:
        model = XCLIPModel.from_pretrained(str(model_dir), local_files_only=True,
                                          trust_remote_code=False, use_safetensors=True)
        processor = XCLIPProcessor.from_pretrained(str(model_dir), local_files_only=True,
                                                   trust_remote_code=False)
        model.eval()
    else:
        model, processor = runtime  # Reuse the same verified CPU weights across benchmark clips.
    cap = cv2.VideoCapture(str(video))
    if not cap.isOpened():
        raise ValueError("Could not decode the selected video using OpenCV.")
    windows = window_grid(count, num, den)
    events = []
    try:
        for start, end in windows:
            samples = frame_indices(start, end)
            frames = []
            for index in samples:
                cap.set(cv2.CAP_PROP_POS_FRAMES, index)
                ok, bgr = cap.read()
                if not ok:
                    raise ValueError("Video frame decoding failed at index " + str(index))
                frames.append(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB))
            # Hugging Face XCLIPProcessor expects the 8-frame video sequence directly.
            # videos=[frames] adds an extra nesting level and can break real preprocessing.
            inputs = processor(text=PROMPTS, videos=frames, return_tensors="pt",
                               padding=True, truncation=True)
            with torch.no_grad():
                logits = model(**inputs).logits_per_video[0]
                scores = torch.softmax(logits, dim=-1).detach().cpu().tolist()
            cue = decide(scores, start, end, min_score, min_margin)
            if traces is not None:
                raw_logits = logits.detach().cpu().tolist()
                ranking = sorted(range(len(scores)), key=lambda i: scores[i], reverse=True)
                top, second = ranking[:2]
                traces.append({
                    "window_start_frame": start, "window_end_frame": end,
                    "decoded_frame_indices": samples,
                    "raw_logits": {action[0]: round(float(raw_logits[i]), 8)
                                   for i, action in enumerate(ACTIONS)},
                    "relative_softmax_scores": {action[0]: round(float(scores[i]), 8)
                                                for i, action in enumerate(ACTIONS)},
                    "ranking": [ACTIONS[i][0] for i in ranking],
                    "top_score": round(float(scores[top]), 8),
                    "top_margin": round(float(scores[top] - scores[second]), 8),
                    "abstention_reason": (
                        "neutral_prompt" if ACTIONS[top][1] == "other"
                        else "low_relative_score" if scores[top] < min_score
                        else "insufficient_margin" if scores[top] - scores[second] < min_margin
                        else None
                    ),
                    "emitted_candidate": cue is not None
                })
            if cue is not None:
                events.append(cue)
    finally:
        cap.release()
    return events, len(windows)


def produce(video: Path, model_dir: Path, ffprobe: Path,
            min_score: float, min_margin: float, classifier=classify,
            diagnostic_scores: bool = False):
    if not video.is_file() or video.is_symlink() or video.suffix.lower() not in {
        ".mp4", ".mov", ".mkv", ".mxf"
    }:
        raise ValueError("Expected a local non-symlink video of a supported type.")
    if not model_dir.is_dir() or model_dir.is_symlink() or not (model_dir / "config.json").is_file():
        raise ValueError("Explicit local X-CLIP model directory with config.json required.")
    if not (model_dir / "model.safetensors").is_file() and not (
        model_dir / "model.safetensors.index.json"
    ).is_file():
        raise ValueError("Local safetensors weights required; downloads/pickle are disallowed.")
    if not ffprobe.is_file() or ffprobe.is_symlink():
        raise ValueError("ffprobe must be an explicit local executable.")
    if not 0.30 <= min_score <= 0.99 or not 0.05 <= min_margin <= 0.95:
        raise ValueError("Relative score and score-margin thresholds out of bounds.")
    num, den, count = probe(ffprobe, video)
    traces = []
    if diagnostic_scores:
        if classifier is not classify:
            raise ValueError("Diagnostics require the actual pretrained classifier.")
        results, windows = classifier(video, model_dir, num, den, count,
                                      min_score, min_margin, traces=traces)
    else:
        results, windows = classifier(video, model_dir, num, den, count,
                                      min_score, min_margin)
    if not isinstance(results, list) or len(results) > MAX_WINDOWS:
        raise ValueError("Provider produced too many candidate windows.")
    stat = video.stat()
    return {
        "provider": "microsoft.xclip.zero_shot_local",
        "detection_type": "video_text_similarity_action_candidates_only",
        "video_path": str(video),
        "video_size_bytes": stat.st_size,
        "model_directory": str(model_dir),
        "model_executed": True,
        "fps_numerator": num,
        "fps_denominator": den,
        "video_frame_count": count,
        "windows_evaluated": windows,
        "sampled_frames_per_window": FRAMES_PER_WINDOW,
        "relative_score_not_calibrated": True,
        "automatic_action_recognition": True,
        "human_review_required": True,
        "sample_audio_identified": False,
        "cues": results,
        **({"window_diagnostics": traces} if diagnostic_scores else {}),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--video", required=True)
    parser.add_argument("--model-dir", required=True)
    parser.add_argument("--ffprobe", required=True)
    parser.add_argument("--min-score", type=float, default=0.48)
    parser.add_argument("--min-margin", type=float, default=0.12)
    parser.add_argument("--diagnostic-scores", action="store_true",
                        help="Include raw model logits and all ten relative prompt scores.")
    args = parser.parse_args()
    video = Path(args.video).resolve(strict=True)
    model_dir = Path(args.model_dir).resolve(strict=True)
    ffprobe = Path(args.ffprobe).resolve(strict=True)
    result = produce(video, model_dir, ffprobe, args.min_score, args.min_margin,
                     diagnostic_scores=args.diagnostic_scores)
    json.dump(result, sys.stdout, ensure_ascii=False, separators=(",", ":"))
    sys.stdout.write("\n")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, RuntimeError, ImportError,
            subprocess.SubprocessError, json.JSONDecodeError) as exc:
        print("XCLIP_ACTION_PROVIDER_REJECTED: " + str(exc), file=sys.stderr)
        sys.exit(2)
