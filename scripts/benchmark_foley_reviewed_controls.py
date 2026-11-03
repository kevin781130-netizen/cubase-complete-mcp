#!/usr/bin/env python3
"""Four hash-locked real-video X-CLIP vs SigLIP action-control evaluation.

PREPARE -> XCLIP -> SIGLIP -> FINALIZE. Models run in separate CPU processes.
Reviewed stills establish 3 narrow positives and 1 door-specific negative.
No general population accuracy claim, sound matching or host DAW changes.
Source video/clip files never leave the isolated Render build workspace.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess
import sys

import benchmark_xclip_commons as benchmark
import foley_door_fusion as door_fusion
import foley_door_motion as door_motion
import compare_siglip_xclip as siglip
import survey_foley_running_door_controls as sources
import xclip_foley_action_provider as xclip

PROOF = Path(".render-xclip-real-proof")
WORK = PROOF / "_reviewed_controls_media_NOT_PUBLISHED"
INPUT_MANIFEST = WORK / "inputs.json"
REVIEW_FIXTURE = Path("tests/fixtures/foley_action_controls_visual_review_20261104.json")
MOTION_ROIS = Path("tests/fixtures/foley_door_motion_rois_v1.json")
POSITIVES = ("arakawa_jogging_at_4", "subway_door_at_4", "mine_door_at_0")
NEGATIVE = "mine_door_at_4"
REQUIRED_IDS = set(POSITIVES) | {NEGATIVE}
DOOR_CLASSES = {"opening_door", "closing_door"}
MODEL_THRESHOLD_SCORE = 0.48
MODEL_THRESHOLD_MARGIN = 0.12


def reviewed_windows():
    fixture = json.loads(REVIEW_FIXTURE.read_text(encoding="utf8"))
    if fixture.get("schema") != "foley-visual-action-controls-review-v1":
        raise ValueError("Unsupported human review fixture.")
    cases = fixture.get("windows", [])
    by_id = {x["id"]: x for x in cases}
    if len(cases) != 6 or len(by_id) != 6:
        raise ValueError("Six unique reviewed windows required.")
    if set(by_id) - REQUIRED_IDS != {"arakawa_jogging_at_0", "subway_door_at_0"}:
        raise ValueError("Unexpected or replaced review windows.")
    if any(by_id[id]["visual_status"] != "confirmed_visible" or
           not by_id[id]["include_in_positive_denominator"] for id in POSITIVES):
        raise ValueError("Three visually confirmed positive windows required.")
    neg = by_id[NEGATIVE]
    if neg["visual_status"] != "confirmed_class_specific_negative" or        neg["class_under_review"] != "opening_or_closing_door" or        neg["include_in_positive_denominator"]:
        raise ValueError("Mine door negative must stay door-specific only.")
    expected = {
        "arakawa_jogging_at_4": "running",
        "subway_door_at_4": "closing_door",
        "mine_door_at_0": "opening_door",
    }
    for id, target in expected.items():
        if by_id[id]["class_under_review"] != target:
            raise ValueError("Review class changed: " + id)
    for id in REQUIRED_IDS:
        row = by_id[id]
        digest = row.get("contact_sheet_sha256")
        if not isinstance(digest, str) or len(digest) != 64 or             any(c not in "0123456789abcdef" for c in digest):
            raise ValueError("Invalid reviewed SHA256: " + id)
        if row.get("sampled_frame_indices") != [0, 5, 9, 14, 18, 23]:
            raise ValueError("Sampled frame positions changed: " + id)
    return {id: by_id[id] for id in (*POSITIVES, NEGATIVE)}


def source_by_id():
    return {x["id"]: x for x in sources.CANDIDATES}


def prepare(ffmpeg: Path, ffprobe: Path):
    if WORK.exists() or not ffmpeg.is_file() or not ffprobe.is_file():
        raise ValueError("Fresh, valid video workspace and binaries required.")
    PROOF.mkdir(parents=True, exist_ok=True)
    WORK.mkdir()
    verified = reviewed_windows()
    sources_by_id = source_by_id()
    entries = []
    # Re-download each source once; no invented video captions or samples.
    for source_id in ("arakawa_jogging", "subway_door", "mine_door"):
        meta = sources_by_id[source_id]
        origin = WORK / (source_id + ".webm")
        source_url = benchmark.commons_file_url(meta["filename"])
        try:
            origin_hash, original_bytes = benchmark.fetch_bounded(
                source_url, origin, max_bytes=15_000_000)
            for id, review in verified.items():
                if review["source_id"] != source_id:
                    continue
                output = WORK / (id + ".mp4")
                start = review["start_seconds"]
                subprocess.run([
                    str(ffmpeg), "-nostdin", "-hide_banner", "-loglevel", "error",
                    "-y", "-ss", str(start), "-i", str(origin),
                    "-t", "2", "-vf", "fps=12,scale=320:-2",
                    "-an", "-c:v", "mpeg4", "-q:v", "5", str(output)
                ], check=True, timeout=75)
                rate_num, rate_den, count = xclip.probe(ffprobe, output)
                if (rate_num, rate_den, count) != (12, 1, 24):
                    raise ValueError("Exact 24 frames at 12fps required for " + id)
                frames, digest = benchmark.emit_frame_contact_sheet(
                    output, id, emit_preview=False)
                if digest != review["contact_sheet_sha256"] or                     frames != review["sampled_frame_indices"]:
                    raise ValueError("SAMPLED_FRAMES_CHANGED_REVIEW_REQUIRED: " + id)
                with output.open("rb") as fh:
                    converted_sha = hashlib.file_digest(fh, "sha256").hexdigest()
                entries.append({
                    "id": id,
                    "source_id": source_id,
                    "source_page": meta["page"],
                    "source_author": meta["creator"],
                    "source_license": meta["license"],
                    "source_sha256": origin_hash,
                    "source_bytes": original_bytes,
                    "video_sha256": converted_sha,
                    "contact_sha256": digest,
                    "frame_rate": "12/1",
                    "video_frame_count": count,
                    "window_start_seconds": start,
                    "human_review_status": review["visual_status"],
                    "reviewed_target": review["class_under_review"],
                })
                print("FOLEY_REVIEWED_VIDEO_LOCKED=" +
                      json.dumps({"id": id, "contact_sha256": digest}), flush=True)
        finally:
            origin.unlink(missing_ok=True)
    if {x["id"] for x in entries} != REQUIRED_IDS:
        raise ValueError("Incomplete reviewed video source set.")
    INPUT_MANIFEST.write_text(
        json.dumps({"review_fixture": str(REVIEW_FIXTURE),
                    "source_sha": subprocess.check_output(
                        ["git", "rev-parse", "HEAD"], text=True).strip(),
                    "entries": entries}, indent=2) + "\n", encoding="utf8")
    print("FOLEY_REVIEWED_FOUR_SOURCE_CLIPS_READY=4/4", flush=True)


def require_inputs():
    data = json.loads(INPUT_MANIFEST.read_text(encoding="utf8"))
    rows = data["entries"]
    if len(rows) != 4 or {row["id"] for row in rows} != REQUIRED_IDS:
        raise ValueError("Four SHA-locked inputs required.")
    for row in rows:
        clip = WORK / (row["id"] + ".mp4")
        with clip.open("rb") as fh:
            current = hashlib.file_digest(fh, "sha256").hexdigest()
        if current != row["video_sha256"]:
            raise ValueError("Video changed after visual approval: " + row["id"])
    return data, rows


def infer_xclip(model_dir: Path, ffprobe: Path):
    from transformers import XCLIPModel, XCLIPProcessor
    import torch
    _, inputs = require_inputs()
    torch.set_num_threads(2)
    model = XCLIPModel.from_pretrained(
        str(model_dir), local_files_only=True, trust_remote_code=False,
        use_safetensors=True)
    processor = XCLIPProcessor.from_pretrained(
        str(model_dir), local_files_only=True, trust_remote_code=False)
    model.eval()
    results = []
    for item in inputs:
        clip = WORK / (item["id"] + ".mp4")
        traces = []
        cues, windows = xclip.classify(
            clip, model_dir, 12, 1, 24, MODEL_THRESHOLD_SCORE,
            MODEL_THRESHOLD_MARGIN, traces=traces, runtime=(model, processor))
        if len(traces) != 1 or windows != 1:
            raise ValueError("Exactly one video-text window required.")
        trace = traces[0]
        if len(trace["ranking"]) != len(xclip.ACTIONS):
            raise ValueError("X-CLIP produced incomplete action rankings.")
        labels = [c["action_class"] for c in cues]
        target = item["reviewed_target"]
        results.append({
            "id": item["id"],
            "video_sha256": item["video_sha256"],
            "contact_sha256": item["contact_sha256"],
            "review": item["human_review_status"],
            "target": target,
            "target_rank": trace["ranking"].index(target) + 1 if item["id"] in POSITIVES else None,
            "top1": trace["ranking"][0],
            "raw_logits": trace["raw_logits"],
            "relative_scores_not_calibrated": trace["relative_softmax_scores"],
            "ranking": trace["ranking"],
            "abstention_reason": trace["abstention_reason"],
            "emitted_candidates": labels,
            "matched_reviewed_positive": target in labels if item["id"] in POSITIVES else None,
            "door_specific_false_alarm_candidate": bool(DOOR_CLASSES.intersection(labels))
                if item["id"] == NEGATIVE else None,
        })
        print("FOLEY_REAL_XCLIP_CONTROL=" + json.dumps({
            "id": item["id"], "top1": trace["ranking"][0],
            "rank": results[-1]["target_rank"],
            "candidates": labels
        }), flush=True)
    (PROOF / "action-controls-xclip.json").write_text(
        json.dumps(results, indent=2) + "\n", encoding="utf8")


def infer_siglip(model_dir: Path, ffprobe: Path):
    from transformers import SiglipModel, SiglipProcessor
    import torch
    _, inputs = require_inputs()
    siglip.verify_model(model_dir)
    torch.set_num_threads(2)
    model = SiglipModel.from_pretrained(
        str(model_dir), local_files_only=True, trust_remote_code=False,
        use_safetensors=True)
    processor = SiglipProcessor.from_pretrained(
        str(model_dir), local_files_only=True, trust_remote_code=False)
    model.eval()
    results = []
    for item in inputs:
        clip = WORK / (item["id"] + ".mp4")
        baseline = siglip.score_frames(clip, model, processor, ffprobe)
        target = item["reviewed_target"]
        results.append({
            "id": item["id"],
            "video_sha256": item["video_sha256"],
            "contact_sha256": item["contact_sha256"],
            "review": item["human_review_status"],
            "target": target,
            "target_rank": baseline["ranking"].index(target) + 1
                if item["id"] in POSITIVES else None,
            "top1": baseline["ranking"][0],
            "ranking": baseline["ranking"],
            "sigmoid_frame_mean_not_calibrated": baseline["class_scores"],
            "door_class_in_top3": bool(DOOR_CLASSES.intersection(baseline["ranking"][:3]))
                if item["id"] == NEGATIVE else None,
            "frame_indices": baseline["frame_indices"],
        })
        print("FOLEY_REAL_SIGLIP_CONTROL=" + json.dumps({
            "id": item["id"], "top1": baseline["ranking"][0],
            "rank": results[-1]["target_rank"],
            "door_class_in_top3": results[-1]["door_class_in_top3"]
        }), flush=True)
    (PROOF / "action-controls-siglip.json").write_text(
        json.dumps(results, indent=2) + "\n", encoding="utf8")


def inspect_door_motion():
    _, inputs = require_inputs()
    fixture = json.loads(MOTION_ROIS.read_text(encoding="utf8"))
    if fixture.get("schema") != "foley-door-motion-rois-v1":
        raise ValueError("Unexpected independent ROI annotation schema.")
    rois = fixture.get("rois", [])
    roi_by_id = {row["id"]: row for row in rois}
    expected_ids = {"subway_door_at_4", "mine_door_at_0", NEGATIVE}
    if len(rois) != 3 or set(roi_by_id) != expected_ids:
        raise ValueError("Exactly three reviewed, independently annotated door ROIs required.")
    measurements = []
    for item in inputs:
        id = item["id"]
        if id not in expected_ids:
            continue
        annotation = roi_by_id[id]
        if annotation.get("source_contact_sha256") != item["contact_sha256"]:
            raise ValueError("ROI annotation does not match reviewed contact-sheet SHA: " + id)
        result = door_motion.examine_video(
            WORK / (id + ".mp4"), annotation["normalized_xyxy"], 24)
        measurements.append({
            "id": id,
            "video_sha256": item["video_sha256"],
            "contact_sha256": item["contact_sha256"],
            "annotation_source": str(MOTION_ROIS),
            "motion": result,
        })
        print("FOLEY_REAL_DOOR_ROI_MOTION=" + json.dumps({
            "id": id, "evidence": result["localized_motion_evidence"],
            "active_pairs": result["active_pairs"],
            "status": result["status"]
        }), flush=True)
    if len(measurements) != 3:
        raise ValueError("Door motion evidence incomplete.")
    (PROOF / "action-controls-motion.json").write_text(
        json.dumps(measurements, indent=2, ensure_ascii=False) + "\n", encoding="utf8")


def summarize(xclip_rows: list[dict], siglip_rows: list[dict]):
    x = {r["id"]: r for r in xclip_rows}
    s = {r["id"]: r for r in siglip_rows}
    if (len(xclip_rows) != 4 or len(siglip_rows) != 4 or
        set(x) != REQUIRED_IDS or set(s) != REQUIRED_IDS):
        raise ValueError("All four, and only four, reviewed segments required.")
    for id in REQUIRED_IDS:
        if x[id]["video_sha256"] != s[id]["video_sha256"] or                 x[id]["contact_sha256"] != s[id]["contact_sha256"]:
            raise ValueError("Compared different media or frame annotations.")
    return {
        "verified_positive_windows": 3,
        "verified_door_operation_specific_negative_windows": 1,
        "xclip_reviewed_positive_top1": sum(x[id]["target_rank"] == 1 for id in POSITIVES),
        "xclip_reviewed_positive_top3": sum(x[id]["target_rank"] <= 3 for id in POSITIVES),
        "xclip_default_threshold_candidate_matches": sum(
            x[id]["matched_reviewed_positive"] is True for id in POSITIVES),
        "siglip_reviewed_positive_top1": sum(s[id]["target_rank"] == 1 for id in POSITIVES),
        "siglip_reviewed_positive_top3": sum(s[id]["target_rank"] <= 3 for id in POSITIVES),
        "xclip_door_specific_negative_wrong_door_candidate": bool(
            x[NEGATIVE]["door_specific_false_alarm_candidate"]),
        "siglip_door_specific_negative_door_class_in_top3": bool(
            s[NEGATIVE]["door_class_in_top3"]),
        "model_score_scales_comparable": False,
        "precision_or_recall": "NOT_ESTIMATED_3_POSITIVES_ONE_NARROW_NEGATIVE",
        "action_onsets_ground_truthed": False,
        "human_visual_review_blinded": False,
        "production_threshold_changed": False,
    }


def finalize():
    info, inputs = require_inputs()
    xr = json.loads((PROOF / "action-controls-xclip.json").read_text(encoding="utf8"))
    sr = json.loads((PROOF / "action-controls-siglip.json").read_text(encoding="utf8"))
    metrics = summarize(xr, sr)
    # Fusion is a separate optional review layer: only model rankings are
    # consulted. Human labels are used for *evaluation*, never for inference.
    fusion_rows = door_fusion.fuse_pairs(xr, sr)
    fusion_summary = door_fusion.summarize(fusion_rows, NEGATIVE)
    motion_rows = json.loads((PROOF / "action-controls-motion.json").read_text(encoding="utf8"))
    if (len(motion_rows) != 3 or
            {r["id"] for r in motion_rows} != {"subway_door_at_4", "mine_door_at_0", NEGATIVE}):
        raise ValueError("Missing or extra door ROI motion evidence.")
    motions = {r["id"]: r for r in motion_rows}
    if motions[NEGATIVE]["motion"]["localized_motion_evidence"] is not False:
        raise ValueError("STATIC_DOOR_ROI_MOTION_CONTROL_FAILED: fail closed.")
    motion_fused = door_motion.apply_motion_gate(fusion_rows, motion_rows)
    motion_summary = {
        "door_roi_motion_method": door_motion.METHOD,
        "reviewed_door_regions": len(motion_rows),
        "class_specific_static_door_negative_motion": "NO_LOCALIZED_MOTION",
        "motion_supported_review_proposals": sum(
            r["proposed_door_action"] is not None for r in motion_fused),
        "unverified_panel_geometry": True,
        "verified_direction": False,
        "automatic_sfx_matching": False,
        "host_modified": False,
    }
    if len(fusion_rows) != len(inputs):
        raise ValueError("Door fusion omitted a reviewed clip.")
    expected = {r["id"]: r for r in inputs}
    for row in xr + sr:
        item = expected[row["id"]]
        if row["video_sha256"] != item["video_sha256"] or                 row["contact_sha256"] != item["contact_sha256"]:
            raise ValueError("Real model result input identity mismatch.")
    document = {
        "status": "PASS_REAL_BOTH_MODELS_FOUR_REVIEWED_VIDEO_WINDOWS_NOT_ACCURACY_VALIDATED",
        "source_sha": info["source_sha"],
        "video_segments": [
            {"id": row["id"], "class_reviewed": row["reviewed_target"],
             "review_status": row["human_review_status"],
             "start_seconds": row["window_start_seconds"],
             "source_page": row["source_page"],
             "source_author": row["source_author"],
             "source_license": row["source_license"],
             "original_sha256": row["source_sha256"],
             "reencoded_sha256": row["video_sha256"],
             "reviewed_contact_sha256": row["contact_sha256"]}
            for row in inputs
        ],
        "xclip_model": "microsoft/xclip-base-patch32",
        "siglip_model": "google/siglip-base-patch16-224",
        "xclip_report": "action-controls-xclip.json",
        "siglip_report": "action-controls-siglip.json",
        "summary": metrics,
        "door_fusion_experimental": {
            "status": "REVIEW_ONLY_NO_AUTO_SOUND",
            "rule": door_fusion.RULE,
            "summary": fusion_summary,
            "rows": fusion_rows,
        },
        "door_roi_motion_experimental": {
            "status": "REVIEW_ONLY_NOT_DOOR_GEOMETRY_VALIDATED",
            "summary": motion_summary,
            "roi_evidence": motion_rows,
            "motion_gated_rows": motion_fused,
        },
        "review_limitation": "6 contact-sheet stills per 2-second clip, nonblind; no full-motion annotation. A no-door-operation window is NOT an absence-of-any-action control.",
        "siglip_limitation": "Still-image scores do not encode action timing. Do not compare sigmoid scores with X-CLIP softmax scores.",
        "model_weights_and_video_published": False,
        "automatic_sfx_matching": "NOT_RUN",
        "cubase_nuendo_windows": "NOT_RUN",
    }
    (PROOF / "action-controls-comparison.json").write_text(
        json.dumps(document, indent=2, ensure_ascii=False) + "\n", encoding="utf8")
    for clip in WORK.glob("*.mp4"):
        clip.unlink()
    INPUT_MANIFEST.unlink()
    WORK.rmdir()
    print("FOLEY_FOUR_REAL_MODEL_COMPARISON_PASS=" + json.dumps(metrics), flush=True)


def main():
    if len(sys.argv) < 2:
        raise SystemExit("Usage: benchmark_foley_reviewed_controls.py prepare|xclip|siglip|finalize [args]")
    stage = sys.argv[1]
    if stage == "prepare" and len(sys.argv) == 4:
        prepare(Path(sys.argv[2]), Path(sys.argv[3]))
    elif stage == "xclip" and len(sys.argv) == 4:
        infer_xclip(Path(sys.argv[2]), Path(sys.argv[3]))
    elif stage == "siglip" and len(sys.argv) == 4:
        infer_siglip(Path(sys.argv[2]), Path(sys.argv[3]))
    elif stage == "motion" and len(sys.argv) == 2:
        inspect_door_motion()
    elif stage == "finalize" and len(sys.argv) == 2:
        finalize()
    else:
        raise SystemExit("Invalid reviewed control benchmark CLI stage or arguments.")


if __name__ == "__main__":
    main()
