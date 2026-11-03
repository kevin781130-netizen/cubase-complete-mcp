"""No-network tests for optional CutPilot visual scene-boundary adapter."""
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock


MODULE_FILE = Path(__file__).resolve().parents[2] / "scripts" / "cutpilot_foley_scene_provider.py"
spec = importlib.util.spec_from_file_location("steinberg_cutpilot_provider_tests", MODULE_FILE)
provider = importlib.util.module_from_spec(spec)
spec.loader.exec_module(provider)


class CutPilotSceneProviderTests(unittest.TestCase):
    def test_fps_fraction_and_count_from_ffprobe(self):
        record = {"streams": [{
            "avg_frame_rate": "30000/1001", "r_frame_rate": "30000/1001", "nb_frames": "120"
        }]}
        with mock.patch.object(provider.subprocess, "run",
                               return_value=SimpleNamespace(stdout=json.dumps(record))) as run:
            self.assertEqual(provider.probe(Path("/tmp/ffprobe"), Path("/tmp/scene.mp4")), (30000, 1001, 120))
            self.assertEqual(run.call_args.kwargs["timeout"], 30)

    def test_variable_fps_is_rejected_without_guessing(self):
        record = {"streams": [{
            "avg_frame_rate": "24000/1001", "r_frame_rate": "30/1", "nb_frames": "300"
        }]}
        with mock.patch.object(provider.subprocess, "run",
                               return_value=SimpleNamespace(stdout=json.dumps(record))):
            with self.assertRaisesRegex(ValueError, "variable-frame-rate"):
                provider.probe(Path("/tmp/ffprobe"), Path("/tmp/scene.mp4"))

    def test_scene_candidates_are_only_visual_and_never_sfx_matches(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            video = folder / "scene.mp4"
            video.write_bytes(b"stub-video-identity")
            with mock.patch.object(provider, "probe", return_value=(30000, 1001, 240)):
                with mock.patch.object(provider, "load_cutpilot",
                                       return_value=lambda *a, **kw: [1.001, 2.002]):
                    doc = provider.build_payload(video, folder, folder / "ffprobe", 0.25, 0.52)
            self.assertEqual(doc["provider"], "cutpilot.scenes.detect_scene_cuts")
            self.assertEqual([cue["frame"] for cue in doc["cues"]], [30, 60])
            self.assertFalse(doc["automatic_action_recognition"])
            self.assertTrue(doc["human_review_required"])
            self.assertTrue(all(cue["track"] == "other" for cue in doc["cues"]))
            self.assertTrue(all("review" in cue["sfx_query"] for cue in doc["cues"]))

    def test_external_scene_detector_import_works_without_copying_cutpilot(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            scenes = root / "cutpilot" / "scenes.py"
            scenes.parent.mkdir()
            scenes.write_text("def detect_scene_cuts(video_path, start, end, **kw):\n"
                              "    return [0.75, 2.0]\n", encoding="utf-8")
            detect = provider.load_cutpilot(root)
            self.assertEqual(detect("video", 0, 3), [0.75, 2.0])

    def test_unbounded_and_unexpected_scene_results_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            video = root / "short.mov"
            video.write_bytes(b"video")
            with mock.patch.object(provider, "probe", return_value=(24, 1, 240)):
                with mock.patch.object(provider, "load_cutpilot", return_value=lambda *a, **kw: [0.5] * 129):
                    with self.assertRaisesRegex(ValueError, "at most 128"):
                        provider.build_payload(video, root, root / "ffprobe", 0.25, 0.52)
                with mock.patch.object(provider, "load_cutpilot", return_value=lambda *a, **kw: [float("nan")]):
                    with self.assertRaisesRegex(ValueError, "non-finite"):
                        provider.build_payload(video, root, root / "ffprobe", 0.25, 0.52)


if __name__ == "__main__":
    unittest.main()
