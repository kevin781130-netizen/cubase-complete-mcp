#!/usr/bin/env python3
"""License-aware survey of Commons action footage; no model or accuracy claims.

Downloads bounded public-domain / CC clips solely to sample deterministic still
contact sheets. All original/derivative videos are deleted. Outputs a review
manifest, with NO assertion of what the sampled frames actually depict.
"""
from __future__ import annotations
import hashlib
import json
from pathlib import Path
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from benchmark_xclip_commons import (commons_file_url, fetch_bounded,
                                    emit_frame_contact_sheet)

SURVEY = (
    {
        "id": "cdc_child_running",
        "filename": "2 Year Milestone- Begins to run.webm",
        "expected_action_from_description": "running",
        "source_page": "https://commons.wikimedia.org/wiki/File:2_Year_Milestone-_Begins_to_run.webm",
        "creator": "Centers for Disease Control and Prevention",
        "license": "Public domain (US federal government/CDC)",
    },
    {
        "id": "walking_in_sands",
        "filename": "Walking in the sands.webm",
        "expected_action_from_description": "walking",
        "source_page": "https://commons.wikimedia.org/wiki/File:Walking_in_the_sands.webm",
        "creator": "sgu18ify",
        "license": "CC BY 3.0",
    },
    {
        "id": "nhs_clapping",
        "filename": "Clap for NHS Thousands clap hands together in round of applause for NHS workers Metro.webm",
        "expected_action_from_description": "clapping",
        "source_page": "https://commons.wikimedia.org/wiki/File:Clap_for_NHS_Thousands_clap_hands_together_in_round_of_applause_for_NHS_workers_Metro.webm",
        "creator": "AZ News",
        "license": "CC BY 3.0 (Commons license-reviewed YouTube media)",
    },
)

def survey(ffmpeg: Path) -> dict:
    root = Path(".render-xclip-real-proof")
    work = root / "_survey_unpublished"
    work.mkdir(parents=True, exist_ok=True)
    cases = []
    for sample in SURVEY:
        name = sample["id"]
        source = work / (name + ".webm")
        converted = work / (name + ".mp4")
        item = {**sample, "clip_start_seconds": 0, "clip_duration_seconds": 2,
                "review_status": "NOT_REVIEWED", "audio_evaluated": False}
        try:
            url = commons_file_url(sample["filename"])
            source_digest, size = fetch_bounded(url, source, max_bytes=35_000_000)
            subprocess.run([str(ffmpeg), "-nostdin", "-hide_banner",
                            "-loglevel", "error", "-y", "-i", str(source),
                            "-t", "2", "-vf", "fps=12,scale=320:-2",
                            "-an", "-c:v", "mpeg4", "-q:v", "5",
                            str(converted)], check=True, timeout=60)
            frames, hashvalue = emit_frame_contact_sheet(converted, name)
            item.update({"status": "CONTACT_SHEET_GENERATED",
                         "download_url": url, "source_sha256": source_digest,
                         "source_bytes": size,
                         "processed_sha256": hashlib.sha256(converted.read_bytes()).hexdigest(),
                         "contact_sheet_sha256": hashvalue,
                         "sampled_frame_indices": frames,
                         "review_status": "AWAITING_INDEPENDENT_VISUAL_REVIEW"})
        except Exception as exc:
            item.update({"status": "SURVEY_FAILED",
                         "failure_type": type(exc).__name__,
                         "failure_reason": str(exc)[:320]})
        finally:
            source.unlink(missing_ok=True)
            converted.unlink(missing_ok=True)
        cases.append(item)
        print("FOLEY_SURVEY_RESULT=" + json.dumps(
            {"id": name, "status": item["status"],
             "contact_hash": item.get("contact_sheet_sha256"),
             "failure": item.get("failure_reason")}),flush=True)
    record = {
        "schema": "steinberg-foley-action-survey-v1",
        "head": subprocess.check_output(["git","rev-parse","HEAD"],text=True).strip(),
        "copyright_notice": "Full footage and extracted contact images not published; source metadata and hashes only",
        "still_review": "NOT_REVIEWED",
        "ground_truth_classification": "NOT_AVAILABLE",
        "xclip_inference": "NOT_RUN_ON_SURVEY_CANDIDATES",
        "samples": cases,
    }
    (root/"survey-report.json").write_text(json.dumps(record,ensure_ascii=False,indent=2)+"\n")
    work.rmdir()
    return record

if __name__=="__main__":
    if len(sys.argv)!=2:
        raise SystemExit("Usage: survey_foley_action_videos.py /abs/path/to/ffmpeg")
    r=survey(Path(sys.argv[1]))
    good=sum(v["status"]=="CONTACT_SHEET_GENERATED" for v in r["samples"])
    print(f"FOLEY_SURVEY_CANDIDATES_READY={good}/{len(r['samples'])}",flush=True)
    if good<2:
        raise SystemExit("SURVEY_BLOCKED_TOO_FEW_VERIFIABLE_VIDEOS")
