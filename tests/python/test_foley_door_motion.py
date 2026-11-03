"""Deterministic OpenCV synthetic time-series + strict real-footage contract tests."""
import importlib.util
import json
from pathlib import Path
import sys
import unittest

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
spec = importlib.util.spec_from_file_location("foley_door_motion_test", ROOT / "scripts/foley_door_motion.py")
motion = importlib.util.module_from_spec(spec)
spec.loader.exec_module(motion)

ROI = [0.30, 0.10, 0.70, 0.90]


def still():
    return np.full((96, 160), 95, dtype=np.uint8)


def moving():
    frames = []
    for i in range(8):
        image = still()
        pos = 55 + 3 * i
        image[20:79, pos:pos + 10] = 220
        frames.append(image)
    return frames


class FoleDoorMotionTests(unittest.TestCase):
    def test_moving_panel_localized_multiple_pairs(self):
        result = motion.analyze_gray_frames(moving(), ROI)
        self.assertEqual(result["status"], "localized_motion_evidence")
        self.assertTrue(result["localized_motion_evidence"])
        self.assertGreaterEqual(result["active_pairs"], 2)
        self.assertFalse(result["door_panel_geometry_verified"])
        self.assertFalse(result["opening_vs_closing_direction_verified"])
        self.assertFalse(result["action_onset_verified"])

    def test_static_frames_never_propose_door_motion(self):
        result = motion.analyze_gray_frames([still() for _ in range(8)], ROI)
        self.assertEqual(result["status"], "insufficient_localized_motion")
        self.assertEqual(result["active_pairs"], 0)
        self.assertFalse(result["localized_motion_evidence"])

    def test_motion_only_outside_door_region_does_not_trigger(self):
        frames = []
        for i in range(8):
            image = still()
            image[20:80, 2 + i*3:12 + i*3] = 225
            frames.append(image)
        result = motion.analyze_gray_frames(frames, ROI)
        self.assertFalse(result["localized_motion_evidence"])

    def test_uniform_brightness_changes_are_not_door_motion(self):
        frames = [np.full((96, 160), 35 + i * 18, dtype=np.uint8) for i in range(8)]
        result = motion.analyze_gray_frames(frames, ROI)
        self.assertFalse(result["localized_motion_evidence"])

    def test_roi_input_must_be_explicit_localized_and_finite(self):
        for invalid in [None, [0,0,1,1], [0,0,0.1,0.8], [0,0,0.6,float("nan")],
                        [0.7,0.1,0.4,0.8], [-0.1,0.1,0.6,0.9]]:
            with self.assertRaises(ValueError):
                motion.analyze_gray_frames(moving(), invalid)

    def test_motion_gate_abstains_when_prior_classifier_abstains(self):
        row = {"id": "mine_door_at_4", "proposed_door_action": None,
               "video_sha256": "a"*64, "contact_sha256": "b"*64}
        result = motion.apply_motion_gate([row], [])
        self.assertEqual(result[0]["status"], "abstained")
        self.assertIsNone(result[0]["selected_audio_file"])
        self.assertEqual(result[0]["host_action"], "none")

    def test_model_consensus_cannot_bypass_missing_or_static_roi(self):
        row = {"id": "mine_door_at_0", "proposed_door_action": "opening_door",
               "video_sha256": "a"*64, "contact_sha256": "b"*64}
        with self.assertRaisesRegex(ValueError, "matching ROI"):
            motion.apply_motion_gate([row], [])
        evidence = {"id": row["id"], "video_sha256": row["video_sha256"],
                    "contact_sha256": row["contact_sha256"],
                    "motion": {"localized_motion_evidence": False}}
        gated = motion.apply_motion_gate([row], [evidence])
        self.assertIsNone(gated[0]["proposed_door_action"])
        self.assertEqual(gated[0]["reason"], "no_independent_roi_motion")
        evidence["motion"]["localized_motion_evidence"] = True
        gated = motion.apply_motion_gate([row], [evidence])
        self.assertEqual(gated[0]["proposed_door_action"], "opening_door")
        self.assertFalse(gated[0]["door_panel_geometry_verified"])

    def test_reviewed_rois_are_sha_bound_to_source_frames(self):
        source = json.loads((ROOT / "tests/fixtures/foley_action_controls_visual_review_20261104.json").read_text())
        fixed = json.loads((ROOT / "tests/fixtures/foley_door_motion_rois_v1.json").read_text())
        refs = {entry["id"]: entry for entry in source["windows"]}
        self.assertEqual(len(fixed["rois"]), 3)
        for entry in fixed["rois"]:
            self.assertEqual(entry["source_contact_sha256"], refs[entry["id"]]["contact_sheet_sha256"])
            self.assertEqual(len(motion.check_roi(entry["normalized_xyxy"])), 4)
        self.assertFalse(any("auto" in str(x["annotation"]).lower() for x in fixed["rois"]))


if __name__ == "__main__":
    unittest.main()
