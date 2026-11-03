#!/usr/bin/env python3
"""Conservative visual *motion evidence* for user-reviewed door ROIs.

Measures spatially localized differences between eight ordered video frames.
A fixed ROI must be supplied independently of the model's predicted class.
This does NOT detect a door panel, distinguish opening from closing, compensate
all camera movements, or establish exact action onset. No sound/DAW actions.
"""
from __future__ import annotations

import math
from pathlib import Path

import cv2
import numpy as np

from xclip_foley_action_provider import frame_indices

METHOD = "door_roi_localized_frame_difference_v1"
MIN_PAIR_FRACTION = 0.035
MIN_ACTIVE_PAIRS = 2
PIXEL_CHANGE_FLOOR = 22
MOTION_ROI_MIN_PIXELS = 24


def check_roi(roi) -> tuple[float, float, float, float]:
    if not isinstance(roi, (list, tuple)) or len(roi) != 4:
        raise ValueError("Explicit normalized door ROI [x0,y0,x1,y1] is required.")
    if any(type(x) not in (int, float) or not math.isfinite(x) for x in roi):
        raise ValueError("ROI must contain four finite numeric coordinates.")
    x0, y0, x1, y1 = map(float, roi)
    if not (0 <= x0 < x1 <= 1 and 0 <= y0 < y1 <= 1):
        raise ValueError("ROI must be within normalized 0..1 image coordinates.")
    if x1 - x0 < 0.15 or y1 - y0 < 0.25 or (x1 - x0) * (y1 - y0) > 0.80:
        raise ValueError("ROI must cover a usable localized door region, not the whole image.")
    return x0, y0, x1, y1


def analyze_gray_frames(frames: list[np.ndarray], roi, indices=None) -> dict:
    """Deterministic 8-frame motion proxy; does not use action or review labels."""
    norm = check_roi(roi)
    if len(frames) != 8:
        raise ValueError("Exactly eight temporally ordered frames required.")
    if any(not isinstance(frame, np.ndarray) or frame.ndim != 2 or
           frame.dtype != np.uint8 or frame.shape != frames[0].shape
           for frame in frames):
        raise ValueError("All frames must be same-size 8-bit grayscale.")
    height, width = frames[0].shape
    if min(height, width) < 64:
        raise ValueError("Video too small for reliable ROI motion proxy.")
    x0, y0, x1, y1 = norm
    xa, ya = round(x0 * width), round(y0 * height)
    xb, yb = round(x1 * width), round(y1 * height)
    if xb - xa < MOTION_ROI_MIN_PIXELS or yb - ya < MOTION_ROI_MIN_PIXELS:
        raise ValueError("Door ROI too small in actual decoded pixels.")
    mask = np.zeros((height, width), dtype=np.bool_)
    mask[ya:yb, xa:xb] = True
    if (~mask).sum() < width * height * 0.15:
        raise ValueError("ROI leaves insufficient outside background for comparison.")

    blurred = [cv2.GaussianBlur(f, (5, 5), 0) for f in frames]
    measures = []
    for i in range(7):
        delta = cv2.absdiff(blurred[i], blurred[i + 1])
        # Robust per-pair threshold: global intensity jitter must not pass.
        background = delta[~mask]
        threshold = max(PIXEL_CHANGE_FLOOR, float(np.median(background)) + 16)
        changed = delta > threshold
        fraction_in = float(changed[mask].mean())
        fraction_out = float(changed[~mask].mean())
        # If most of the scene moves, it is not independent ROI evidence.
        camera_or_scene_motion = (
            fraction_out > 0.16 and fraction_in < fraction_out * 1.5
        )
        local_motion = (
            fraction_in >= MIN_PAIR_FRACTION and
            not camera_or_scene_motion and
            fraction_in > fraction_out * 1.1
        )
        measures.append({
            "pair": i,
            "frame_start": indices[i] if indices else i,
            "frame_end": indices[i + 1] if indices else i + 1,
            "changed_fraction_inside_roi": round(fraction_in, 5),
            "changed_fraction_outside_roi": round(fraction_out, 5),
            "threshold_8bit": round(threshold, 2),
            "broad_camera_or_scene_motion": camera_or_scene_motion,
            "localized_motion_pair": local_motion,
        })
    count = sum(v["localized_motion_pair"] for v in measures)
    present = count >= MIN_ACTIVE_PAIRS
    return {
        "method": METHOD,
        "roi_normalized_xyxy": list(norm),
        "status": "localized_motion_evidence" if present else "insufficient_localized_motion",
        "localized_motion_evidence": present,
        "active_pairs": count,
        "required_active_pairs": MIN_ACTIVE_PAIRS,
        "pairs": measures,
        "door_panel_geometry_verified": False,
        "opening_vs_closing_direction_verified": False,
        "action_onset_verified": False,
        "camera_motion_fully_compensated": False,
        "requires_human_review": True,
    }


def examine_video(path: Path, roi, expected_frame_count: int = 24) -> dict:
    check_roi(roi)
    cap = cv2.VideoCapture(str(path))
    if not cap.isOpened():
        raise ValueError("Could not decode local door video.")
    indices = frame_indices(0, expected_frame_count)
    frames = []
    try:
        if int(cap.get(cv2.CAP_PROP_FRAME_COUNT)) != expected_frame_count:
            raise ValueError("Frame count changed since the video review.")
        for index in indices:
            cap.set(cv2.CAP_PROP_POS_FRAMES, index)
            valid, bgr = cap.read()
            if not valid:
                raise ValueError("Could not read reviewed video frame " + str(index))
            frames.append(cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY))
    finally:
        cap.release()
    result = analyze_gray_frames(frames, roi, indices)
    result["sampled_frame_indices"] = indices
    return result


def apply_motion_gate(consensus_rows: list[dict], motion_rows: list[dict]) -> list[dict]:
    """Experimental additional guard. Missing/untrusted ROI evidence abstains."""
    motion = {r["id"]: r for r in motion_rows}
    if len(motion) != len(motion_rows):
        raise ValueError("Duplicated motion evidence ID.")
    output = []
    for row in consensus_rows:
        id = row["id"]
        evidence = motion.get(id)
        if row["proposed_door_action"] is not None:
            if evidence is None or evidence.get("video_sha256") != row["video_sha256"] or (
                evidence.get("contact_sha256") != row["contact_sha256"]
            ):
                raise ValueError("Positive door proposal lacks matching ROI evidence.")
            is_supported = evidence["motion"]["localized_motion_evidence"] is True
            action = row["proposed_door_action"] if is_supported else None
            reason = "door_consensus_plus_localized_motion" if is_supported else "no_independent_roi_motion"
        else:
            action = None
            reason = "prior_model_consensus_abstained"
        output.append({
            "id": id,
            "video_sha256": row["video_sha256"],
            "contact_sha256": row["contact_sha256"],
            "status": "review_proposal_not_confirmed" if action else "abstained",
            "proposed_door_action": action,
            "reason": reason,
            "door_panel_geometry_verified": False,
            "direction_verified_by_motion": False,
            "action_onset_verified": False,
            "requires_human_review": True,
            "selected_audio_file": None,
            "host_action": "none",
        })
    return output
