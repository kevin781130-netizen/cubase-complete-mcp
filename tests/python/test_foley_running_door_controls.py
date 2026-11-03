"""Pure no-network source contract: never confuse description with action truth."""
from pathlib import Path
import importlib.util
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
spec = importlib.util.spec_from_file_location(
    "foley_action_controls_survey",
    Path(__file__).resolve().parents[2] / "scripts" / "survey_foley_running_door_controls.py"
)
controls = importlib.util.module_from_spec(spec)
spec.loader.exec_module(controls)


class FoleyRunningDoorSurveyTests(unittest.TestCase):
    def test_includes_separate_running_and_door_sources(self):
        cases = controls.CANDIDATES
        self.assertEqual(len(cases), 3)
        self.assertEqual({x["intended_class"] for x in cases},
                         {"running", "closing_door", "opening_door"})
        for x in cases:
            self.assertEqual(x["start_offsets_seconds"], [0, 4])
            self.assertTrue(x["license"].startswith("CC BY"))
            self.assertIn("commons.wikimedia.org", x["page"])
            self.assertTrue(x["creator"])

    def test_negative_is_only_no_clapping_not_all_no_motion(self):
        neg = controls.NEGATIVE
        self.assertEqual(neg["target"], "no_visible_clapping_in_first_two_seconds")
        self.assertEqual(len(neg["frame_contact_sha256"]), 64)
        self.assertIn("NOT proof of zero motion", neg["visual_review"])
        self.assertEqual(neg["automatic_inference"], "NOT_RUN")

    def test_running_and_door_have_two_temporal_opportunities(self):
        for x in controls.CANDIDATES:
            self.assertEqual(len(set(x["start_offsets_seconds"])), 2)
            self.assertTrue(all(t >= 0 for t in x["start_offsets_seconds"]))

    def test_reviewer_not_model_assigns_ground_truth(self):
        source = (Path(__file__).resolve().parents[2] /
                  "scripts/survey_foley_running_door_controls.py").read_text()
        self.assertIn('"PENDING_FOLLOW_UP_REVIEW"', source)
        self.assertIn('"NOT_ESTABLISHED_FROM_TITLE"', source)
        self.assertIn('"NOT_RUN"', source)
        self.assertIn('"model_threshold_changes": False', source)


if __name__ == "__main__":
    unittest.main()
