"""No-model contract tests for four SHA-locked real-video model comparisons."""
from copy import deepcopy
from pathlib import Path
import importlib.util
import sys
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
spec = importlib.util.spec_from_file_location(
    "reviewed_action_controls", ROOT / "scripts/benchmark_foley_reviewed_controls.py")
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


class ReviewedActionControlsTests(unittest.TestCase):
    def test_exact_three_confirmed_positive_and_one_specific_negative(self):
        inputs = mod.reviewed_windows()
        self.assertEqual(set(inputs), mod.REQUIRED_IDS)
        self.assertEqual(len(mod.POSITIVES), 3)
        self.assertEqual(inputs["arakawa_jogging_at_4"]["class_under_review"], "running")
        self.assertEqual(inputs["subway_door_at_4"]["class_under_review"], "closing_door")
        self.assertEqual(inputs["mine_door_at_0"]["class_under_review"], "opening_door")
        self.assertEqual(inputs[mod.NEGATIVE]["visual_status"],
                         "confirmed_class_specific_negative")
        for id, review in inputs.items():
            self.assertEqual(len(review["contact_sheet_sha256"]), 64)
            self.assertEqual(review["sampled_frame_indices"], [0, 5, 9, 14, 18, 23])

    def test_metrics_never_include_indeterminate_clips(self):
        x_rows = [
            self.row(id, rank=i, match=(id == "arakawa_jogging_at_4"),
                     alarm=(id == "mine_door_at_4"))
            for i, id in enumerate((*mod.POSITIVES, mod.NEGATIVE), start=1)
        ]
        s_rows = [
            self.row(id, rank=i, match=False, alarm=(id == "mine_door_at_4"))
            for i, id in enumerate((*mod.POSITIVES, mod.NEGATIVE), start=1)
        ]
        metrics = mod.summarize(x_rows, s_rows)
        self.assertEqual(metrics["verified_positive_windows"], 3)
        self.assertEqual(metrics["verified_door_operation_specific_negative_windows"], 1)
        self.assertEqual(metrics["xclip_default_threshold_candidate_matches"], 1)
        self.assertTrue(metrics["xclip_door_specific_negative_wrong_door_candidate"])
        self.assertTrue(metrics["siglip_door_specific_negative_door_class_in_top3"])
        self.assertEqual(metrics["precision_or_recall"], "NOT_ESTIMATED_3_POSITIVES_ONE_NARROW_NEGATIVE")
        self.assertFalse(metrics["model_score_scales_comparable"])
        self.assertFalse(metrics["production_threshold_changed"])

    def test_mismatch_between_models_refused(self):
        x_rows = [self.row(id, rank=1) for id in (*mod.POSITIVES, mod.NEGATIVE)]
        s_rows = deepcopy(x_rows)
        s_rows[0]["contact_sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "different media"):
            mod.summarize(x_rows, s_rows)

    def test_missing_negative_or_unreviewed_extra_refused(self):
        rows = [self.row(id, rank=1) for id in mod.POSITIVES]
        with self.assertRaisesRegex(ValueError, "reviewed segments required"):
            mod.summarize(rows, rows)

    def test_source_titles_cannot_replace_reviewed_labels(self):
        path = mod.REVIEW_FIXTURE
        with mock.patch.object(mod, "REVIEW_FIXTURE", ROOT / path):
            expected = mod.reviewed_windows()
        self.assertEqual(len(expected), 4)
        self.assertNotIn("arakawa_jogging_at_0", expected)
        self.assertNotIn("subway_door_at_0", expected)

    @staticmethod
    def row(id, rank=1, match=False, alarm=False):
        return {
            "id": id, "target_rank": rank,
            "video_sha256": "a" * 64, "contact_sha256": "b" * 64,
            "matched_reviewed_positive": match,
            "door_specific_false_alarm_candidate": alarm,
            "door_class_in_top3": alarm
        }


if __name__ == "__main__":
    unittest.main()
