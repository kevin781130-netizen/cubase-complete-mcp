"""Offline contract tests: NO actual X-CLIP model inference is claimed."""
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

FILE = Path(__file__).resolve().parents[2] / "scripts" / "xclip_foley_action_provider.py"
spec = importlib.util.spec_from_file_location("steinberg_xclip_candidate_provider_test", FILE)
provider = importlib.util.module_from_spec(spec)
spec.loader.exec_module(provider)


class XclipActionProviderTests(unittest.TestCase):
    def test_real_video_model_is_not_loaded_during_contract_tests(self):
        with tempfile.TemporaryDirectory() as tmp:
            directory = Path(tmp)
            video = directory / "test.mp4"
            video.write_bytes(b"placeholder-video-no-real-decoding")
            model = directory / "model"
            model.mkdir()
            (model / "config.json").write_text("{}", encoding="utf-8")
            (model / "model.safetensors").write_bytes(b"placeholder-weights")
            probe = directory / "ffprobe"
            probe.write_bytes(b"placeholder-binary")
            def simulated(video, model, n, d, count, min_score, min_margin):
                self.assertEqual((n, d, count), (30000, 1001, 120))
                return [{
                    "frame": 30, "duration_frames": 0,
                    "event_label": "Possible action: walking",
                    "track": "footsteps", "sfx_query": "review possible footsteps",
                    "action_class": "walking", "relative_score": 0.8,
                    "score_margin": 0.7, "window_start_frame": 0,
                    "window_end_frame": 60,
                }], 2
            with mock.patch.object(provider, "probe", return_value=(30000, 1001, 120)):
                out = provider.produce(video, model, probe, 0.48, 0.12,
                                       classifier=simulated)
            self.assertTrue(out["human_review_required"])
            self.assertTrue(out["relative_score_not_calibrated"])
            self.assertFalse(out["sample_audio_identified"])
            self.assertEqual(out["provider"], "microsoft.xclip.zero_shot_local")
            self.assertEqual(out["cues"][0]["track"], "footsteps")
            self.assertEqual(out["windows_evaluated"], 2)

    def test_model_missing_safetensors_refused_without_loading(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            video = root / "video.mov"
            video.write_bytes(b"file")
            model = root / "model"
            model.mkdir()
            (model / "config.json").write_text("{}", encoding="utf8")
            ffprobe = root / "ffprobe"
            ffprobe.write_bytes(b"x")
            with self.assertRaisesRegex(ValueError, "safetensors"):
                provider.produce(video, model, ffprobe, 0.48, 0.12)

    def test_ffprobe_detects_inconsistent_rational_rates(self):
        record = {"streams": [{"avg_frame_rate": "30000/1001", "r_frame_rate": "24/1",
                               "nb_frames": "240"}]}
        with mock.patch.object(provider.subprocess, "run",
                               return_value=SimpleNamespace(stdout=json.dumps(record))):
            with self.assertRaisesRegex(ValueError, "VFR"):
                provider.probe(Path("/tmp/ffprobe"), Path("/tmp/movie.mp4"))

    def test_window_frame_indices_are_bounded_and_deterministic(self):
        windows = provider.window_grid(120, 30000, 1001)
        self.assertEqual(windows, [(0, 60), (60, 120)])
        self.assertEqual(provider.frame_indices(0, 60)[0], 0)
        self.assertEqual(provider.frame_indices(0, 60)[-1], 59)
        with self.assertRaisesRegex(ValueError, "too few"):
            provider.frame_indices(0, 5)

    def test_conservative_abstention_and_coarse_window_center(self):
        scores = [0.77, 0.07, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02]
        cue = provider.decide(scores, 0, 60, 0.48, 0.12)
        self.assertEqual(cue["action_class"], "walking")
        self.assertEqual(cue["frame"], 29)
        self.assertEqual(cue["duration_frames"], 0)
        self.assertEqual(cue["window_end_frame"], 60)
        self.assertIsNone(provider.decide(scores, 0, 60, 0.9, 0.12))
        self.assertIsNone(provider.decide([0.49, 0.47] + [0.005] * 8, 0, 60, 0.48, 0.12))
        neutral = [0.02] * 8 + [0.7, 0.14]
        self.assertIsNone(provider.decide(neutral, 0, 60, 0.48, 0.12))

    def test_invalid_probabilities_and_unbounded_input_refused(self):
        with self.assertRaisesRegex(ValueError, "invalid"):
            provider.decide([float("nan")] * 10, 0, 60, 0.48, 0.12)
        record = {"streams": [{"avg_frame_rate": "24/1", "r_frame_rate": "24/1",
                               "nb_frames": "744"}]}
        with mock.patch.object(provider.subprocess, "run",
                               return_value=SimpleNamespace(stdout=json.dumps(record))):
            with self.assertRaisesRegex(ValueError, "30 seconds"):
                provider.probe(Path("/tmp/ffprobe"), Path("/tmp/movie.mp4"))


if __name__ == "__main__":
    unittest.main()
