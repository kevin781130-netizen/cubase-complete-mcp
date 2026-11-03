#!/usr/bin/env python3
"""Conservative, auditable door-action proposal gate for paired X-CLIP + SigLIP.

Both independently run models MUST rank the same directional door action first.
This suppresses the measured stationary-door SigLIP false positive when X-CLIP
ranks quiet_scene first. This is not motion tracking or probability calibration:
no accepted proposal constitutes a verified action or automatic audio placement.
The human-review label/ground truth is NEVER an input to gate decisions.
"""
from __future__ import annotations

import json
from pathlib import Path
import sys

from xclip_foley_action_provider import ACTIONS

DOOR_CLASSES = frozenset({"opening_door", "closing_door"})
ALL_CLASSES = frozenset(row[0] for row in ACTIONS)
RULE = "directional_door_exact_top1_consensus_v1"


def _ranked(row: dict, model: str) -> list[str]:
    ranks = row.get("ranking")
    if not isinstance(ranks, list) or len(ranks) != len(ALL_CLASSES) or (
        set(ranks) != ALL_CLASSES or any(not isinstance(x, str) for x in ranks)
    ):
        raise ValueError(f"{model}: invalid, missing or duplicated action ranking")
    if row.get("top1") != ranks[0]:
        raise ValueError(f"{model}: top1 must match ranking[0]")
    return ranks


def combine_one(xclip: dict, siglip: dict) -> dict:
    """Combine only independent model ranks, never reviewer class labels."""
    for key in ("id", "video_sha256", "contact_sha256"):
        left, right = xclip.get(key), siglip.get(key)
        if not isinstance(left, str) or left != right or not left:
            raise ValueError("Cannot fuse results from mismatched source or identity: " + key)
    for key in ("video_sha256", "contact_sha256"):
        if len(xclip[key]) != 64 or any(c not in "0123456789abcdef" for c in xclip[key]):
            raise ValueError("Invalid source checksum: " + key)
    xr = _ranked(xclip, "X-CLIP")
    sr = _ranked(siglip, "SigLIP")
    x_top, s_top = xr[0], sr[0]

    # No ground-truth class, timecode, sound name, or manual positive label is
    # consulted. If the classifiers disagree, fail closed with zero door cues.
    if x_top not in DOOR_CLASSES:
        reason = "xclip_top1_not_directional_door"
        candidate = None
    elif s_top != x_top:
        reason = "model_disagreement_on_door_direction"
        candidate = None
    else:
        reason = "both_models_rank_same_directional_door_first"
        candidate = x_top

    return {
        "id": xclip["id"],
        "video_sha256": xclip["video_sha256"],
        "contact_sha256": xclip["contact_sha256"],
        "rule": RULE,
        "xclip_top1": x_top,
        "siglip_top1": s_top,
        "status": "review_proposal_not_confirmed" if candidate else "abstained",
        "proposed_door_action": candidate,
        "reason": reason,
        "confidence_calibrated": False,
        "verified_motion": False,
        "requires_human_review": True,
        "selected_audio_file": None,
        "sound_effect_auto_placement": False,
        "host_action": "none",
    }


def fuse_pairs(x_rows: list[dict], s_rows: list[dict]) -> list[dict]:
    if not isinstance(x_rows, list) or not isinstance(s_rows, list):
        raise ValueError("Both model result sets must be lists")
    if len(x_rows) != len(s_rows) or not x_rows:
        raise ValueError("Model result sets must be non-empty and equal length")
    x = {row.get("id"): row for row in x_rows}
    s = {row.get("id"): row for row in s_rows}
    if len(x) != len(x_rows) or len(s) != len(s_rows) or set(x) != set(s):
        raise ValueError("Missing, duplicate or unmatched paired video results")
    return [combine_one(x[id], s[id]) for id in sorted(x)]


def summarize(cues: list[dict], negative_id: str) -> dict:
    if not cues or len({row["id"] for row in cues}) != len(cues):
        raise ValueError("Unique non-empty fusion results required")
    negative = next((row for row in cues if row["id"] == negative_id), None)
    if negative is None:
        raise ValueError("Required class-specific negative is missing")
    if negative["proposed_door_action"] is not None:
        # An observed negative receiving a door cue must never be declared PASS
        raise ValueError("DOOR_STATIC_NEGATIVE_FALSE_PROPOSAL")
    return {
        "rule": RULE,
        "window_count": len(cues),
        "door_review_proposals": sum(row["proposed_door_action"] is not None for row in cues),
        "abstentions": sum(row["proposed_door_action"] is None for row in cues),
        "class_specific_static_door_negative": "ABSTAINED_NO_DOOR_PROPOSAL",
        "motion_verified": False,
        "action_onset_verified": False,
        "thresholds_changed": False,
        "automatic_sfx_matching": False,
        "automatic_daw_placement": False,
        "commercial_accuracy_validated": False,
    }


def main() -> None:
    if len(sys.argv) != 5:
        raise SystemExit("Usage: foley_door_fusion.py XCLIP.json SIGLIP.json NEGATIVE_ID OUTPUT.json")
    xfile, sfile, negative_id, output = sys.argv[1:]
    xrows = json.loads(Path(xfile).read_text(encoding="utf8"))
    srows = json.loads(Path(sfile).read_text(encoding="utf8"))
    joined = fuse_pairs(xrows, srows)
    summary = summarize(joined, negative_id)
    path = Path(output)
    if path.exists():
        raise ValueError("Output already exists: refuse silent overwrite")
    path.write_text(json.dumps({
        "schema": "foley-door-consensus-review-v1",
        "status": "experimental_review_only",
        "summary": summary,
        "rows": joined,
        "sfx_files": [],
        "host_action": "none",
    }, indent=2, ensure_ascii=False) + "\n", encoding="utf8")
    print("FOLEY_DOOR_CONSENSUS_GATE_PASS=" + json.dumps(summary), flush=True)


if __name__ == "__main__":
    main()
