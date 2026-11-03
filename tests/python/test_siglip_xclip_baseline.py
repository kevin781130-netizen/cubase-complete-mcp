"""No-model contract checks for the paired real-SigLIP versus X-CLIP gate."""
import importlib.util
from pathlib import Path
import unittest
from unittest import mock
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
spec = importlib.util.spec_from_file_location(
    "paired_siglip_contract_tests",
    Path(__file__).resolve().parents[2] / "scripts" / "compare_siglip_xclip.py"
)
paired = importlib.util.module_from_spec(spec)
spec.loader.exec_module(paired)


class SiglipBaselineContractTests(unittest.TestCase):
    def test_only_visually_reviewed_positive_videos_are_in_denominator(self):
        rows = [
            {"review_status": "confirmed_visible", "siglip_target_rank": 1, "xclip_target_rank": 2},
            {"review_status": "confirmed_visible", "siglip_target_rank": 4, "xclip_target_rank": 3},
            {"review_status": "not_confirmed", "siglip_target_rank": 1, "xclip_target_rank": 1},
            {"review_status": "indeterminate", "siglip_target_rank": 1, "xclip_target_rank": 1},
        ]
        report = paired.summarize(rows)
        self.assertEqual(report["reviewed_visible_positive_excerpts"], 2)
        self.assertEqual(report["siglip_top1_matches_reviewed_positive"], 1)
        self.assertEqual(report["siglip_top3_matches_reviewed_positive"], 1)
        self.assertEqual(report["xclip_top3_matches_reviewed_positive"], 2)
        self.assertEqual(report["dataset_accuracy"], "NOT_ESTIMATED_N_2_NONBLIND_STILL_REVIEW")
        self.assertFalse(report["production_threshold_changed"])
        self.assertFalse(report["thresholds_comparable_across_models"])

    def test_missing_or_wrong_model_hash_refused(self):
        from tempfile import TemporaryDirectory
        with TemporaryDirectory() as d:
            model = Path(d)
            with self.assertRaisesRegex(ValueError, "missing"):
                paired.verify_model(model)
            (model / "model.safetensors").write_bytes(b"not real weights")
            with self.assertRaisesRegex(ValueError, "mismatch"):
                paired.verify_model(model)

    def test_partial_case_set_never_counts_as_success(self):
        with self.assertRaisesRegex(ValueError, "Four sources"):
            paired.summarize([{"review_status": "confirmed_visible"}])

    def test_fixed_provenance_matches_published_model_source(self):
        self.assertEqual(paired.MODEL_REPO, "google/siglip-base-patch16-224")
        self.assertEqual(len(paired.MODEL_SHA256), 64)
        self.assertTrue(all(c in "0123456789abcdef" for c in paired.MODEL_SHA256))


if __name__ == "__main__":
    unittest.main()
