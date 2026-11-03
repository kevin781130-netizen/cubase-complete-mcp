"""Safety checks for fail-closed X-CLIP/SigLIP door proposals.

No actual model inference here; Render gate separately runs the pinned weights
and feeds the true four-video outputs into the very same module.
"""
from copy import deepcopy
from pathlib import Path
import importlib.util
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
spec = importlib.util.spec_from_file_location("foley_door_fusion", ROOT / "scripts" / "foley_door_fusion.py")
fusion = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fusion)

CLASSES = [r[0] for r in fusion.ACTIONS]


def row(id, first, other=None):
    ranks = [first] + [x for x in CLASSES if x != first]
    # Place door test target second when requested without changing top1.
    if other is not None:
        ranks.remove(other)
        ranks.insert(1, other)
    return {
        "id": id, "video_sha256": "a" * 64, "contact_sha256": "b" * 64,
        "ranking": ranks, "top1": first,
    }


class DoorFusionTests(unittest.TestCase):
    def test_real_observation_pattern_blocks_static_door_false_alarm(self):
        # SigLIP ranked opening_door first in a known inactive mine-door clip,
        # but X-CLIP ranked quiet_scene first. Gate must not emit a door event.
        decision = fusion.combine_one(
            row("mine_door_at_4", "quiet_scene"),
            row("mine_door_at_4", "opening_door"))
        self.assertEqual(decision["status"], "abstained")
        self.assertEqual(decision["reason"], "xclip_top1_not_directional_door")
        self.assertIsNone(decision["proposed_door_action"])
        self.assertFalse(decision["verified_motion"])
        self.assertIsNone(decision["selected_audio_file"])
        self.assertEqual(decision["host_action"], "none")

    def test_opening_consensus_only_generates_review_proposal(self):
        result = fusion.combine_one(
            row("mine_door_at_0", "opening_door"),
            row("mine_door_at_0", "opening_door"))
        self.assertEqual(result["status"], "review_proposal_not_confirmed")
        self.assertEqual(result["proposed_door_action"], "opening_door")
        self.assertTrue(result["requires_human_review"])
        self.assertFalse(result["sound_effect_auto_placement"])
        self.assertFalse(result["confidence_calibrated"])

    def test_subway_close_with_xclip_walking_abstains(self):
        result = fusion.combine_one(
            row("subway_door_at_4", "walking", other="closing_door"),
            row("subway_door_at_4", "opening_door", other="closing_door"))
        self.assertEqual(result["status"], "abstained")
        self.assertIsNone(result["proposed_door_action"])

    def test_direction_disagreement_abstains(self):
        result = fusion.combine_one(
            row("door", "closing_door"), row("door", "opening_door"))
        self.assertEqual(result["reason"], "model_disagreement_on_door_direction")
        self.assertIsNone(result["proposed_door_action"])

    def test_missing_duplicate_or_forged_evidence_aborts(self):
        x = row("one", "opening_door")
        s = deepcopy(x)
        s["video_sha256"] = "c" * 64
        with self.assertRaisesRegex(ValueError, "mismatched source"):
            fusion.combine_one(x, s)
        with self.assertRaisesRegex(ValueError, "duplicate"):
            fusion.fuse_pairs([x, x], [x, x])
        with self.assertRaisesRegex(ValueError, "ranking"):
            fusion.combine_one({**x, "ranking": ["opening_door"] * 10}, x)
        with self.assertRaisesRegex(ValueError, "ranking"):
            fusion.combine_one({**x, "top1": "quiet_scene"}, x)

    def test_negative_gate_cannot_false_pass(self):
        bad = fusion.combine_one(
            row("mine_door_at_4", "opening_door"),
            row("mine_door_at_4", "opening_door"))
        with self.assertRaisesRegex(ValueError, "DOOR_STATIC_NEGATIVE_FALSE_PROPOSAL"):
            fusion.summarize([bad], "mine_door_at_4")

    def test_four_observed_windows_are_fail_closed_and_no_audio(self):
        rows = [
            ("arakawa_jogging_at_4", "running", "running"),
            ("subway_door_at_4", "walking", "opening_door"),
            ("mine_door_at_0", "opening_door", "opening_door"),
            ("mine_door_at_4", "quiet_scene", "opening_door"),
        ]
        xr = [row(k, x) for k, x, _ in rows]
        sr = [row(k, s) for k, _, s in rows]
        decisions = fusion.fuse_pairs(xr, sr)
        self.assertEqual(len(decisions), 4)
        self.assertEqual([r["proposed_door_action"] for r in decisions if r["id"] == "mine_door_at_0"],
                         ["opening_door"])
        report = fusion.summarize(decisions, "mine_door_at_4")
        self.assertEqual(report["door_review_proposals"], 1)
        self.assertEqual(report["abstentions"], 3)
        self.assertEqual(report["class_specific_static_door_negative"], "ABSTAINED_NO_DOOR_PROPOSAL")
        self.assertFalse(report["thresholds_changed"])
        self.assertFalse(report["automatic_daw_placement"])


if __name__ == "__main__":
    unittest.main()
