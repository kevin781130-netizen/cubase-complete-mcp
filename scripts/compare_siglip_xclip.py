#!/usr/bin/env python3
"""Real pretrained SigLIP image-frame baseline vs real X-CLIP video ranks.

Limited to 4 already-surveyed, SHA-256-locked two-second Commons excerpts.
SigLIP is a still-image model; it cannot infer the precise onset of video actions.
No audio matching, automatic threshold adjustment, or accuracy claims.
"""
from __future__ import annotations
import hashlib
import json
from pathlib import Path
import subprocess
import sys

import benchmark_xclip_commons as benchmark
import xclip_foley_action_provider as xclip

MODEL_REPO = "google/siglip-base-patch16-224"
MODEL_REVISION = "88ce39778b70932de29ec01792536fc86c2cfdbf"
MODEL_SHA256 = "2c63cb7d1f2e95ba501893cbb8faeb4ea9a3af295498d35097126228659c2af8"


def verify_model(model: Path):
    weight = model / "model.safetensors"
    if not weight.is_file() or weight.is_symlink():
        raise ValueError("Pinned SigLIP safetensors missing.")
    with weight.open("rb") as stream:
        actual = hashlib.file_digest(stream, "sha256").hexdigest()
    if actual != MODEL_SHA256:
        raise ValueError("SigLIP weight SHA256 mismatch: " + actual)
    return actual


def score_frames(video: Path, model, processor):
    import cv2
    import torch
    num, den, count = xclip.probe(Path(sys.argv[3]), video)
    grid = xclip.window_grid(count, num, den)
    # Fixed two-second dataset must create exactly one 8-frame X-CLIP window.
    if len(grid) != 1:
        raise ValueError("Mismatch in window grid for paired model evaluation.")
    indices = xclip.frame_indices(*grid[0])
    capture = cv2.VideoCapture(str(video))
    if not capture.isOpened():
        raise ValueError("Failed to decode video for SigLIP comparison.")
    images = []
    try:
        for n in indices:
            capture.set(cv2.CAP_PROP_POS_FRAMES, n)
            valid, frame = capture.read()
            if not valid:
                raise ValueError("SigLIP frame could not be decoded: " + str(n))
            images.append(cv2.cvtColor(frame, cv2.COLOR_BGR2RGB))
    finally:
        capture.release()
    inputs = processor(
        text=xclip.PROMPTS, images=images, padding="max_length", return_tensors="pt"
    )
    with torch.inference_mode():
        logits = model(**inputs).logits_per_image
        if tuple(logits.shape) != (len(indices), len(xclip.ACTIONS)):
            raise ValueError("SigLIP logits shape mismatch.")
        scores = torch.sigmoid(logits).mean(dim=0).cpu().tolist()
    if not all(0 <= float(score) <= 1 for score in scores):
        raise ValueError("Invalid SigLIP sigmoid scores.")
    classes = [action[0] for action in xclip.ACTIONS]
    ranking = sorted(range(len(scores)), key=lambda i: scores[i], reverse=True)
    return {
        "model_type": "eight_independent_images_not_a_temporal_video_model",
        "score_kind": "mean_of_eight_independent_sigmoid_outputs_not_calibrated",
        "frame_indices": indices,
        "class_scores": {label: round(float(scores[i]), 8) for i, label in enumerate(classes)},
        "ranking": [classes[i] for i in ranking],
    }


def summarize(rows):
    verified = [r for r in rows if r["review_status"] == "confirmed_visible"]
    if len(rows) != 4 or len(verified) != 2:
        raise ValueError("Four sources and two independently reviewed positives required.")
    return {
        "four_source_excerpts": len(rows),
        "reviewed_visible_positive_excerpts": len(verified),
        "siglip_top1_matches_reviewed_positive": sum(r["siglip_target_rank"] == 1 for r in verified),
        "siglip_top3_matches_reviewed_positive": sum(r["siglip_target_rank"] <= 3 for r in verified),
        "xclip_top1_matches_reviewed_positive": sum(r["xclip_target_rank"] == 1 for r in verified),
        "xclip_top3_matches_reviewed_positive": sum(r["xclip_target_rank"] <= 3 for r in verified),
        "baseline_is_still_image_only": True,
        "dataset_accuracy": "NOT_ESTIMATED_N_2_NONBLIND_STILL_REVIEW",
        "thresholds_comparable_across_models": False,
        "production_threshold_changed": False,
    }


def run(model_dir: Path, ffmpeg: Path, ffprobe: Path):
    from transformers import SiglipModel, SiglipProcessor
    import torch

    report_dir = Path(".render-xclip-real-proof")
    previous = json.loads((report_dir / "diagnostic-report.json").read_text(encoding="utf8"))
    known = {r["id"]: r for r in previous["samples"]}
    reviews = benchmark.load_frame_reviews(Path("tests/fixtures/xclip_visual_review_v1.json"))
    if set(known) != {s["id"] for s in benchmark.SAMPLES}:
        raise ValueError("X-CLIP/visual-review source set mismatch.")
    actual = verify_model(model_dir)
    torch.set_num_threads(2)
    model = SiglipModel.from_pretrained(str(model_dir), local_files_only=True,
                                       trust_remote_code=False, use_safetensors=True)
    processor = SiglipProcessor.from_pretrained(str(model_dir), local_files_only=True,
                                                trust_remote_code=False)
    model.eval()
    work = report_dir / "_comparison_media_not_published"
    work.mkdir(exist_ok=True)
    cases = []
    for sample in benchmark.SAMPLES:
        original = work / (sample["id"] + ".webm")
        clip = work / (sample["id"] + "-2s.mp4")
        try:
            source_hash, _ = benchmark.fetch_bounded(
                benchmark.commons_file_url(sample["filename"]), original)
            if source_hash != known[sample["id"]]["source_sha256"]:
                raise ValueError("Commons video changed since X-CLIP inference.")
            subprocess.run(
                [str(ffmpeg), "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                 "-i", str(original), "-t", "2", "-vf", "fps=12,scale=320:-2",
                 "-an", "-c:v", "mpeg4", "-q:v", "5", str(clip)],
                check=True, timeout=90,
            )
            with clip.open("rb") as stream:
                derived_hash = hashlib.file_digest(stream, "sha256").hexdigest()
            if derived_hash != known[sample["id"]]["processed_sha256"]:
                raise ValueError("Derived video differs from original X-CLIP evidence.")
            baseline = score_frames(clip, model, processor)
            target = sample["label"]
            xs = known[sample["id"]]
            xrank = xs["best_label_rank"]
            srank = baseline["ranking"].index(target) + 1
            row = {
                "id": sample["id"],
                "weak_label_from_title": target,
                "review_status": reviews[sample["id"]]["visual_status"],
                "review_contact_sha256": reviews[sample["id"]]["contact_sheet_sha256"],
                "source_sha256": source_hash,
                "derived_video_sha256": derived_hash,
                "source_url": sample["page"],
                "license": sample["license"],
                "xclip_target_rank": xrank,
                "siglip_target_rank": srank,
                "xclip_default_candidate_matches_label": xs["default_threshold_candidate_matches_weak_label"],
                "siglip_frame_baseline": baseline,
                "reviewed_positive_included": reviews[sample["id"]]["qualifies_for_reviewed_subset"],
            }
            cases.append(row)
            print("SIGLIP_PAIRED_CLIP_RESULT=" + json.dumps({
                "id": sample["id"], "weak_label": target,
                "review_status": row["review_status"],
                "xclip_rank": xrank, "siglip_rank": srank
            }), flush=True)
        finally:
            original.unlink(missing_ok=True)
            clip.unlink(missing_ok=True)
    work.rmdir()
    output = {
        "status": "PASS_REAL_SIGLIP_FRAMEWISE_BASELINE_NOT_ACTION_ACCURACY",
        "source_sha": subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip(),
        "xclip_diagnostic_source_sha": previous["source_head"],
        "model_repo": MODEL_REPO,
        "model_revision": MODEL_REVISION,
        "model_license": "Apache-2.0 per Hugging Face model card",
        "model_safetensors_sha256": actual,
        "real_pretrained_cpu_inference": True,
        "same_source_and_decoded_clips_as_xclip": True,
        "sampling": "Same eight frame indices as X-CLIP per two-second excerpt",
        "comparison_limitation": "SigLIP scores single still frames; X-CLIP sees temporal sequence. Sigmoid versus softmax scores cannot be compared numerically.",
        "ground_truth": "Six sampled stills, nonblind. Only two confirmed walking excerpts; two other source-labels unconfirmed.",
        "no_inference_about_running_or_door_quality": True,
        "cases": cases,
        "reviewed_positive_summary": summarize(cases),
        "real_cubase_nuendo_import": "NOT_RUN",
        "automatic_sound_sample_matching": "NOT_RUN",
    }
    (report_dir / "siglip-comparison.json").write_text(
        json.dumps(output, ensure_ascii=False, indent=2) + "\n", encoding="utf8")
    print("SIGLIP_REAL_BASELINE_GATE_PASS " +
          json.dumps(output["reviewed_positive_summary"]), flush=True)


def main():
    if len(sys.argv) != 4:
        raise SystemExit("Usage: compare_siglip_xclip.py MODEL_DIR FFMPEG FFPROBE")
    run(Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3]))


if __name__ == "__main__":
    main()
