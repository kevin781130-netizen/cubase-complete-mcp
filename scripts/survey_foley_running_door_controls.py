#!/usr/bin/env python3
"""License-aware, multi-offset video action contact-sheet survey.

This is an independent VISUAL SOURCE SELECTION gate, not X-CLIP, SigLIP,
ground-truth labeling, or Foley matching. Media is downloaded temporarily,
strictly bounded, sampled at 12fps, and removed before public publication.
Each contact sheet is emitted only in Render build logs for manual review.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from benchmark_xclip_commons import commons_file_url, fetch_bounded, emit_frame_contact_sheet

CANDIDATES = (
    {
        "id": "arakawa_jogging",
        "filename": "Jogging - near arakawa river - tokyo japan - 2022 may 3.webm",
        "intended_class": "running",
        "creator": "Nesnad",
        "license": "CC BY 4.0",
        "page": "https://commons.wikimedia.org/wiki/File:Jogging_-_near_arakawa_river_-_tokyo_japan_-_2022_may_3.webm",
        "start_offsets_seconds": [0, 4],
    },
    {
        "id": "subway_door",
        "filename": "R211 Door Closing.webm",
        "intended_class": "closing_door",
        "creator": "Schvaxet",
        "license": "CC BY-SA 4.0",
        "page": "https://commons.wikimedia.org/wiki/File:R211_Door_Closing.webm",
        "start_offsets_seconds": [0, 4],
    },
    {
        "id": "mine_door",
        "filename": "IDRIJA ANTHONY`S SHAFT; OPENING AND CLOSING THE DOOR TO THE MINE.webm",
        "intended_class": "opening_door",
        "creator": "Sounds of Changes",
        "license": "CC BY 3.0",
        "page": "https://commons.wikimedia.org/wiki/File:IDRIJA_ANTHONY%60S_SHAFT%3B_OPENING_AND_CLOSING_THE_DOOR_TO_THE_MINE.webm",
        "start_offsets_seconds": [0, 4],
    },
)
NEGATIVE = {
    "id": "nhs_intro_no_clapping",
    "target": "no_visible_clapping_in_first_two_seconds",
    "source_page": "https://commons.wikimedia.org/wiki/File:Clap_for_NHS_Thousands_clap_hands_together_in_round_of_applause_for_NHS_workers_Metro.webm",
    "source_license": "CC BY 3.0",
    "frame_contact_sha256": "a3352ba56bba47fe4048ac4fa9702db421e97b6f08973fdf5a658354559780af",
    "visual_review": "The first two seconds show intro graphics, not clapping. This is a candidate no-clapping control, NOT proof of zero motion and NOT a checked video negative for all eight action classes.",
    "prior_provenance": "tests/fixtures/xclip_candidate_survey_20261104.json",
    "automatic_inference": "NOT_RUN",
}


def survey(ffmpeg: Path) -> dict:
    if not ffmpeg.is_file():
        raise ValueError("Explicit ffmpeg binary not found")
    root = Path(".render-xclip-real-proof")
    work = root / "_controls_unpublished"
    work.mkdir(parents=True, exist_ok=True)
    candidates = []
    try:
        for item in CANDIDATES:
            original = work / (item["id"] + ".webm")
            entry = {k: v for k, v in item.items() if k != "filename"}
            entry["status"] = "UNVERIFIED_VISUAL_CANDIDATE"
            entry["windows"] = []
            try:
                source_url = commons_file_url(item["filename"])
                src_digest, src_bytes = fetch_bounded(source_url, original, max_bytes=15_000_000)
                entry.update({"source_url": source_url, "source_sha256": src_digest,
                              "source_bytes": src_bytes})
                for start in item["start_offsets_seconds"]:
                    clip = work / f"{item['id']}_at_{start}.mp4"
                    try:
                        # Source seek followed by fixed CFR; every window is independently
                        # reencoded and must have all 24 frames before review.
                        subprocess.run([
                            str(ffmpeg), "-nostdin", "-hide_banner", "-loglevel", "error",
                            "-y", "-ss", str(start), "-i", str(original),
                            "-t", "2", "-vf", "fps=12,scale=320:-2",
                            "-an", "-c:v", "mpeg4", "-q:v", "5", str(clip)
                        ], check=True, timeout=75)
                        frame_nums, frame_digest = emit_frame_contact_sheet(
                            clip, f"{item['id']}_at_{start}"
                        )
                        with clip.open("rb") as file:
                            clip_digest = hashlib.file_digest(file, "sha256").hexdigest()
                        entry["windows"].append({
                            "start_seconds": start,
                            "length_seconds": 2,
                            "frame_rate": "12/1",
                            "sampled_frame_indices": frame_nums,
                            "processed_video_sha256": clip_digest,
                            "contact_sheet_sha256": frame_digest,
                            "review_status": "AWAITING_INDEPENDENT_VISUAL_REVIEW",
                            "model_inference": "NOT_RUN",
                        })
                    except (OSError, subprocess.SubprocessError, ValueError) as error:
                        entry["windows"].append({
                            "start_seconds": start, "status": "WINDOW_UNAVAILABLE",
                            "error": str(error)[:250],
                            "review_status": "NOT_REVIEWABLE",
                        })
                    finally:
                        clip.unlink(missing_ok=True)
                entry["status"] = (
                    "CONTACT_SHEETS_READY" if any("contact_sheet_sha256" in x
                                                  for x in entry["windows"])
                    else "NO_REVIEWABLE_WINDOWS"
                )
            except (OSError, ValueError, RuntimeError) as error:
                entry["status"] = "SOURCE_UNAVAILABLE"
                entry["error"] = str(error)[:250]
            finally:
                original.unlink(missing_ok=True)
            print("FOLEY_CONTROL_SOURCE=" + json.dumps({
                "id": entry["id"], "status": entry["status"],
                "window_starts": [w["start_seconds"] for w in entry["windows"]
                                  if "contact_sheet_sha256" in w],
            }), flush=True)
            candidates.append(entry)
    finally:
        for file in work.glob("*"):
            if file.is_file(): file.unlink()
        work.rmdir()
    output = {
        "schema": "steinberg-foley-running-door-controls-survey-v1",
        "source_head": subprocess.check_output(
            ["git", "rev-parse", "HEAD"], text=True
        ).strip(),
        "reusable_no_clapping_negative_candidate": NEGATIVE,
        "candidates": candidates,
        "license_provenance": "See each Wikimedia Commons file page, retain attribution",
        "visual_annotation": "PENDING_FOLLOW_UP_REVIEW",
        "action_truth": "NOT_ESTABLISHED_FROM_TITLE",
        "real_xclip_siglip_on_new_controls": "NOT_RUN",
        "model_threshold_changes": False,
        "published_video_or_contact_images": False,
        "note": "Model comparison is not updated until action-positive/negative frame evidence is manually inspected.",
    }
    (root / "action-controls-survey.json").write_text(
        json.dumps(output, indent=2, ensure_ascii=False) + "\n", encoding="utf8"
    )
    ready = sum(x["status"] == "CONTACT_SHEETS_READY" for x in candidates)
    print(f"FOLEY_CONTROL_SURVEY_READY={ready}/{len(CANDIDATES)}", flush=True)
    if ready < 2:
        raise RuntimeError("Insufficient valid candidate videos for action review")
    return output


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: survey_foley_running_door_controls.py ABSOLUTE_FFMPEG")
    survey(Path(sys.argv[1]))
