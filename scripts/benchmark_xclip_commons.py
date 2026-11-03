#!/usr/bin/env python3
"""Small, non-training X-CLIP diagnostic on three Commons video descriptions.

Rights: videos are CC BY-SA. Filenames/categories provide *weak* labels;
the actual first 2 seconds have NOT been temporally annotated. No dataset
precision/recall or commercial accuracy claim can be inferred from three clips.
Weights/video are not published with the JSON report.
"""
from __future__ import annotations
import base64
import hashlib
import json
import math
import os
from pathlib import Path
import subprocess
import sys
from urllib.parse import quote
from urllib.request import Request, urlopen

sys.path.insert(0, str(Path(__file__).resolve().parent))
import xclip_foley_action_provider as provider

# Three independently hosted clips, selected for the descriptions on Commons.
# Source and attribution metadata are retained in each evaluation record.
SAMPLES = (
    {
        "id": "crowd_walking",
        "filename": "Vespero en Hong Kong Island (2014) 02.webm",
        "label": "walking", "creator": "RG72", "license": "CC-BY-SA-4.0",
        "page": "https://commons.wikimedia.org/wiki/File:Vespero_en_Hong_Kong_Island_(2014)_02.webm",
    },
    {
        "id": "runners",
        "filename": "Runners.webm",
        "label": "running", "creator": "Evworo", "license": "CC-BY-SA-3.0",
        "page": "https://commons.wikimedia.org/wiki/File:Runners.webm",
    },
    {
        "id": "fans_clapping",
        "filename": "Viking clapping of Esteghlal fans in the match against Shahr Khodro, 9 May 2022.webm",
        "label": "clapping", "creator": "Maometto", "license": "CC-BY-SA-4.0",
        "page": "https://commons.wikimedia.org/wiki/File:Viking_clapping_of_Esteghlal_fans_in_the_match_against_Shahr_Khodro,_9_May_2022.webm",
    },
)
# Existing production settings remain intact; other thresholds are diagnostic only.
THRESHOLDS = (("current", 0.48, 0.12), ("conservative", 0.60, 0.20),
              ("exploratory", 0.30, 0.05))


def commons_file_url(filename: str) -> str:
    canonical = filename.replace(" ", "_")
    prefix = hashlib.md5(canonical.encode("utf-8")).hexdigest()
    return ("https://upload.wikimedia.org/wikipedia/commons/" + prefix[0] + "/" +
            prefix[:2] + "/" + quote(canonical, safe="()!._-,"))


def fetch_bounded(url: str, target: Path, max_bytes: int = 35_000_000):
    request = Request(url, headers={"User-Agent":
        "SteinbergFoleyMCP/0.1 (video model benchmark; https://github.com/kevin781130-netizen/cubase-complete-mcp)"})
    size = 0
    digest = hashlib.sha256()
    with urlopen(request, timeout=45) as response, target.open("xb") as output:
        if response.status != 200:
            raise RuntimeError("Commons response was not HTTP 200.")
        while True:
            data = response.read(128 * 1024)
            if not data:
                break
            size += len(data)
            if size > max_bytes:
                raise RuntimeError("Commons media exceeds the 35 MB bound.")
            digest.update(data)
            output.write(data)
    if size < 10_000:
        raise RuntimeError("Unexpectedly small media download.")
    return digest.hexdigest(), size


def emit_frame_contact_sheet(clip: Path, label: str):
    """Return true sampled frame indices and log an ephemeral, inspectable JPEG.

    This is NOT ground truth. Frame previews appear only in restricted build logs,
    not in published Render assets or in the GitHub repository.
    """
    import cv2
    import numpy as np

    cap = cv2.VideoCapture(str(clip))
    if not cap.isOpened():
        raise ValueError("Unable to open media for independent frame review.")
    count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    if count < 8:
        cap.release()
        raise ValueError("Too few frames to audit.")
    frames = [round(i * (count - 1) / 5) for i in range(6)]
    canvas = np.full((2 * 145, 3 * 248, 3), 238, dtype=np.uint8)
    try:
        for position, n in enumerate(frames):
            cap.set(cv2.CAP_PROP_POS_FRAMES, n)
            ok, frame = cap.read()
            if not ok:
                raise ValueError("Failed to read frame " + str(n))
            h, w = frame.shape[:2]
            if h < 16 or w < 16:
                raise ValueError("Video too small for human auditing.")
            ratio = min(240 / w, 125 / h)
            resized = cv2.resize(frame, (max(1, int(w * ratio)), max(1, int(h * ratio))))
            r, c = divmod(position, 3)
            x = c * 248 + (248 - resized.shape[1]) // 2
            y = r * 145 + 12 + (125 - resized.shape[0]) // 2
            canvas[y:y + resized.shape[0], x:x + resized.shape[1]] = resized
            cv2.putText(canvas, f"frame={n}", (c * 248 + 4, r * 145 + 11),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.34, (0, 0, 0), 1, cv2.LINE_AA)
    finally:
        cap.release()
    ok, encoded = cv2.imencode(".jpg", canvas, [cv2.IMWRITE_JPEG_QUALITY, 70])
    if not ok:
        raise ValueError("Could not encode independent frame contact sheet.")
    payload = encoded.tobytes()
    if len(payload) > 180000:
        raise ValueError("Frame review sheet exceeded safety size.")
    digest = hashlib.sha256(payload).hexdigest()
    chunks = [base64.b64encode(payload).decode("ascii")[i:i+1800]
              for i in range(0, len(base64.b64encode(payload).decode("ascii")), 1800)]
    print(f"XCLIP_FRAME_CONTACT_META={label}:{len(chunks)}:{digest}", flush=True)
    for i, chunk in enumerate(chunks):
        print(f"XCLIP_FRAME_CONTACT_CHUNK={label}:{i}:{chunk}", flush=True)
    return frames, digest


def predicted_action(trace: dict, min_score: float, min_margin: float):
    top = trace["ranking"][0]
    if top in {"idle_speech", "quiet_scene"}:
        return None
    if trace["top_score"] < min_score or trace["top_margin"] < min_margin:
        return None
    return top


def summarize_case(label: str, traces: list[dict], produced: list[dict]):
    if not traces:
        raise ValueError("A real inference run must contain at least one window.")
    for trace in traces:
        logits = trace["raw_logits"]
        scores = trace["relative_softmax_scores"]
        if set(logits) != {r[0] for r in provider.ACTIONS} or set(scores) != set(logits):
            raise ValueError("All ten prompt logits and scores are required.")
        if not all(math.isfinite(float(v)) for v in logits.values()):
            raise ValueError("Non-finite model logit.")
        if abs(sum(scores.values()) - 1.0) > 0.00001:
            raise ValueError("Relative probabilities are not normalized.")
    expected_ranks = [row["ranking"].index(label) + 1 for row in traces]
    correct_event = any(cue["action_class"] == label for cue in produced)
    unexpected_events = [cue["action_class"] for cue in produced if cue["action_class"] != label]
    return {
        "weak_label": label,
        "best_label_rank": min(expected_ranks),
        "weak_label_top_1": any(rank == 1 for rank in expected_ranks),
        "weak_label_top_3": any(rank <= 3 for rank in expected_ranks),
        "default_threshold_candidate_matches_weak_label": correct_event,
        "possible_missed_weak_label_at_default_threshold": not correct_event,
        "other_candidate_labels_at_default_threshold": unexpected_events,
        "all_raw_window_diagnostics": traces,
        "candidate_events": produced,
    }


def aggregate(rows: list[dict]):
    n = len(rows)
    if n < 1:
        raise ValueError("At least one case is required.")
    top1 = sum(r["weak_label_top_1"] for r in rows)
    top3 = sum(r["weak_label_top_3"] for r in rows)
    hits = sum(r["default_threshold_candidate_matches_weak_label"] for r in rows)
    sweep = []
    for name, score_min, margin_min in THRESHOLDS:
        clip_hits = sum(any(predicted_action(trace, score_min, margin_min) == row["weak_label"]
                            for trace in row["all_raw_window_diagnostics"]) for row in rows)
        unexpected = sum(any(
            predicted_action(trace, score_min, margin_min) not in (None, row["weak_label"])
            for trace in row["all_raw_window_diagnostics"]) for row in rows)
        sweep.append({
            "threshold_name": name,
            "min_relative_score": score_min,
            "min_score_margin": margin_min,
            "weak_label_match_count": clip_hits,
            "possible_miss_count": n - clip_hits,
            "wrong_class_candidate_clip_count": unexpected,
        })
    return {
        "weak_labelled_sample_count": n,
        "top1_match_count": top1,
        "top3_match_count": top3,
        "default_threshold_match_count": hits,
        "possible_miss_count": n - hits,
        "possible_miss_fraction": round((n - hits) / n, 4),
        "threshold_sweep_observational_only": sweep,
        "pre_registration": "No production threshold changes based on this tiny, weak-label sample.",
    }


def main():
    if len(sys.argv) != 4:
        raise SystemExit("Usage: benchmark_xclip_commons.py MODEL_DIR FFMPEG FFPROBE")
    model_dir, ffmpeg, ffprobe = map(Path, sys.argv[1:])
    root = Path(".render-xclip-real-proof")
    root.mkdir(exist_ok=True)
    work = root / "_benchmark_media_not_published"
    work.mkdir(exist_ok=True)
    import torch
    from transformers import XCLIPModel, XCLIPProcessor
    torch.set_num_threads(2)
    # One real model instance for all three source clips, no second weight download.
    model = XCLIPModel.from_pretrained(str(model_dir), local_files_only=True,
                                      trust_remote_code=False, use_safetensors=True)
    processor = XCLIPProcessor.from_pretrained(str(model_dir), local_files_only=True,
                                               trust_remote_code=False)
    model.eval()
    rows = []
    for sample in SAMPLES:
        name = sample["id"]
        original = work / (name + ".webm")
        clip = work / (name + "-2s.mp4")
        source = commons_file_url(sample["filename"])
        print("XCLIP_WEAK_LABEL_SOURCE=" + source, flush=True)
        source_hash, size = fetch_bounded(source, original)
        # Fixed first two seconds, not selected using hidden model predictions.
        subprocess.run(
            [str(ffmpeg), "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
             "-i", str(original), "-t", "2", "-vf", "fps=12,scale=320:-2",
             "-an", "-c:v", "mpeg4", "-q:v", "5", str(clip)],
            check=True, timeout=90,
        )
        numerator, denominator, frame_count = provider.probe(ffprobe, clip)
        if not (frame_count >= 8 and frame_count <= 48):
            raise ValueError("Reencoded sample has unexpected frame count.")
        audited_frames, contact_hash = emit_frame_contact_sheet(clip, name)
        traces = []
        cues, windows = provider.classify(clip, model_dir, numerator, denominator,
                                          frame_count, 0.48, 0.12,
                                          traces=traces, runtime=(model, processor))
        result = summarize_case(sample["label"], traces, cues)
        result.update({
            "id": name, "description_label_is_weak_not_temporally_annotated": True,
            "source_page": sample["page"], "download_url": source,
            "source_author": sample["creator"], "source_license": sample["license"],
            "attribution_notice": "Source: " + sample["creator"] +
                " on Wikimedia Commons. Cropped to the first 2 seconds; reencoded to 12fps 320px.",
            "source_sha256": source_hash, "source_bytes": size,
            "processed_sha256": hashlib.sha256(clip.read_bytes()).hexdigest(),
            "fps": f"{numerator}/{denominator}", "frames": frame_count,
            "window_count": windows,
            "contact_sheet_sha256": contact_hash,
            "contact_sheet_frame_indices": audited_frames,
            "visual_verification_status": "AWAITING_INDEPENDENT_HUMAN_FRAME_REVIEW",
        })
        rows.append(result)
        print("XCLIP_WEAK_LABEL_RESULT=" +
              json.dumps({"id": name, "weak_label": sample["label"],
                          "best_rank": result["best_label_rank"],
                          "candidate_matches_label": result["default_threshold_candidate_matches_weak_label"],
                          "candidate_count": len(cues)}), flush=True)
        # Keep only diagnostic outputs; no reuse/distribution of adapted media.
        original.unlink()
        clip.unlink()
    report = {
        "status": "PASS_REAL_THREE_CLIP_DIAGNOSTIC_WEAK_LABELS_NOT_ACCURACY_VALIDATION",
        "source_head": subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip(),
        "model": "microsoft/xclip-base-patch32",
        "real_pretrained_inference": True,
        "samples": rows,
        "summary": aggregate(rows),
        "sample_labels": "Wikimedia Commons titles/descriptions only; selected first 2 seconds unverified by a human",
        "ground_truth_temporal_coverage": "NOT_ANNOTATED",
        "measured_dataset_recall_or_precision": "NOT_CLAIMED",
        "action_onset_alignment": "NOT_EVALUATED",
        "samples_must_not_be_redistributed_without_license_compliance": True,
        "copyright": "CC-BY-SA originals are attributed; test video derivatives removed, not published",
        "automatic_sfx_matching": "NOT_RUN",
        "cubase_nuendo_import": "NOT_RUN",
    }
    (root / "diagnostic-report.json").write_text(
        json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf8")
    print("XCLIP_THREE_CLIP_DIAGNOSTIC_COMPLETE " +
          json.dumps({"source_head": report["source_head"], **report["summary"]}), flush=True)


if __name__ == "__main__":
    main()
