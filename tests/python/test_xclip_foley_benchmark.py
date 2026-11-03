"""No-model tests for the diagnostic metric contract, not for action recognition."""
import importlib.util
from pathlib import Path
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "benchmark_xclip_commons.py"
spec = importlib.util.spec_from_file_location("xclip_weak_label_benchmark", SCRIPT)
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


class WeakLabelBenchmarkTests(unittest.TestCase):
    def make_trace(self, top: str, second: str, first_score: float, second_score: float):
        actions = [action[0] for action in mod.provider.ACTIONS]
        rest = [x for x in actions if x not in (top, second)]
        scores = {action: 0.0 for action in actions}
        scores[top] = first_score
        scores[second] = second_score
        for action in rest:
            scores[action] = (1.0 - first_score - second_score) / len(rest)
        return {
            "raw_logits": {x: 0.0 for x in actions},
            "relative_softmax_scores": scores,
            "ranking": [top, second] + rest,
            "top_score": first_score,
            "top_margin": first_score - second_score,
            "emitted_candidate": False,
        }

    def test_benchmark_reports_miss_without_inventing_success(self):
        trace = self.make_trace("running", "walking", 0.7, 0.2)
        case = mod.summarize_case("walking", [trace], [])
        self.assertEqual(case["best_label_rank"], 2)
        self.assertFalse(case["weak_label_top_1"])
        self.assertTrue(case["weak_label_top_3"])
        self.assertTrue(case["possible_missed_weak_label_at_default_threshold"])
        metrics = mod.aggregate([case])
        self.assertEqual(metrics["possible_miss_count"], 1)
        self.assertEqual(metrics["top1_match_count"], 0)
        self.assertEqual(metrics["top3_match_count"], 1)

    def test_threshold_sweep_never_mutates_production_defaults(self):
        trace = self.make_trace("walking", "running", 0.5, 0.36)
        self.assertEqual(mod.predicted_action(trace, 0.48, 0.12), "walking")
        self.assertIsNone(mod.predicted_action(trace, 0.60, 0.20))
        self.assertIsNone(mod.predicted_action(self.make_trace("quiet_scene", "walking", 0.7, 0.15), 0.30, 0.05))

    def test_licence_and_sources_are_attributed(self):
        self.assertEqual(len(mod.SAMPLES), 4)
        for sample in mod.SAMPLES:
            self.assertIn("commons.wikimedia.org", sample["page"])
            self.assertIn("CC-BY-SA", sample["license"])
            self.assertTrue(sample["creator"])

    def test_human_review_manifest_keeps_unconfirmed_actions_out_of_denominator(self):
        reviews = mod.load_frame_reviews(
            Path(__file__).resolve().parents[1] / "fixtures" / "xclip_visual_review_v1.json"
        )
        self.assertEqual(reviews["crowd_walking"]["visual_status"], "confirmed_visible")
        self.assertEqual(reviews["runners"]["visual_status"], "not_confirmed")
        self.assertEqual(reviews["fans_clapping"]["visual_status"], "indeterminate")
        self.assertEqual(reviews["walking_in_sands"]["visual_status"], "confirmed_visible")
        rows = [
            {"id": "crowd_walking", "visual_review": reviews["crowd_walking"],
             "default_threshold_candidate_matches_weak_label": False},
            {"id": "runners", "visual_review": reviews["runners"],
             "default_threshold_candidate_matches_weak_label": False},
            {"id": "fans_clapping", "visual_review": reviews["fans_clapping"],
             "default_threshold_candidate_matches_weak_label": True},
            {"id": "walking_in_sands", "visual_review": reviews["walking_in_sands"],
             "default_threshold_candidate_matches_weak_label": False}
        ]
        audited = mod.summarize_reviewed_subset(rows)
        self.assertEqual(audited["confirmed_visible_positive_clip_count"], 2)
        self.assertEqual(audited["confirmed_visible_default_threshold_match_count"], 0)
        self.assertEqual(audited["confirmed_visible_default_threshold_potential_miss_count"], 2)
        self.assertEqual(audited["excluded_unconfirmed_clip_count"], 2)
        self.assertEqual(audited["dataset_recall"], "NOT_ESTIMATED")
        self.assertFalse(audited["production_threshold_changed"])

    def test_human_reviews_must_cover_each_case_and_match_source_labels(self):
        reviews = mod.load_frame_reviews(
            Path(__file__).resolve().parents[1] / "fixtures" / "xclip_visual_review_v1.json")
        self.assertEqual(set(reviews), {x["id"] for x in mod.SAMPLES})
        self.assertEqual(len({v["contact_sheet_sha256"] for v in reviews.values()}), 4)
        self.assertTrue(all(v["sampled_frame_indices"] == [0, 5, 9, 14, 18, 23]
                            for v in reviews.values()))

    def test_invalid_or_partial_score_vectors_fail(self):
        trace = self.make_trace("walking", "running", 0.6, 0.3)
        trace["relative_softmax_scores"].pop("walking")
        with self.assertRaisesRegex(ValueError, "All ten"):
            mod.summarize_case("walking", [trace], [])


if __name__ == "__main__":
    unittest.main()
