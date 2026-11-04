#!/usr/bin/env python3
"""Actual OpenCV video decoder/scene-detector integration smoke with synthetic encoded MP4.
Loads CutPilot scenes.py from a pinned immutable upstream Git revision and checks
the Git blob SHA. Does NOT establish event recognition or camera footage quality.
No CutPilot code is copied into this repository, and no DAW is accessed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import shutil
import subprocess
import sys
import tempfile
import urllib.request
from pathlib import Path

CUTPILOT_HEAD = "c534cd56e8507288b20c1d55fcb5b8d9e8229ea4"
CUTPILOT_SCENES_BLOB = "2c4fd2e9a931358f3dc2420b82c63c07d2807ce5"
CUTPILOT_SOURCE = (
    "https://raw.githubusercontent.com/kevin781130-netizen/CutPilot/"
    + CUTPILOT_HEAD + "/cutpilot/scenes.py"
)


def checked_run(argv: list[str], *, timeout: int = 45) -> subprocess.CompletedProcess:
    process = subprocess.run(
        argv, capture_output=True, text=True, timeout=timeout, check=False
    )
    if process.returncode != 0:
        raise RuntimeError(
            f"COMMAND_FAILED exit={process.returncode} command={Path(argv[0]).name} "
            + process.stderr[-600:]
        )
    return process


def download_exact_scenes(destination: Path) -> None:
    req = urllib.request.Request(
        CUTPILOT_SOURCE, headers={"User-Agent": "Steinberg-Portable-Foley-E2E/1"}
    )
    with urllib.request.urlopen(req, timeout=25) as response:
        source = response.read(96 * 1024 + 1)
    if not source or len(source) > 96 * 1024:
        raise RuntimeError("CUTPILOT_SOURCE_SIZE_INVALID")
    git_blob = hashlib.sha1(
        b"blob " + str(len(source)).encode("ascii") + b"\0" + source
    ).hexdigest()
    if git_blob != CUTPILOT_SCENES_BLOB:
        raise RuntimeError("CUTPILOT_UPSTREAM_GIT_BLOB_MISMATCH")
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(source)


def create_encoded_video(path: Path):
    import cv2
    import numpy as np

    fps = 24
    frame_count_each = 30
    size = (320, 180)
    video = cv2.VideoWriter(str(path), cv2.VideoWriter_fourcc(*"mp4v"), fps, size)
    if not video.isOpened():
        raise RuntimeError("OPENCV_REAL_MP4_ENCODER_UNAVAILABLE")
    try:
        # Three distinct colored scenes, each with a moving block: actual
        # decoded H/S histogram discontinuities at frames 30 and 60.
        for color in ((0, 0, 255), (0, 255, 0), (255, 0, 0)):
            for index in range(frame_count_each):
                frame = np.full((size[1], size[0], 3), color, dtype=np.uint8)
                cv2.rectangle(
                    frame, (index * 5, 50), (index * 5 + 30, 90),
                    (255, 255, 255), -1
                )
                video.write(frame)
    finally:
        video.release()
    if not path.is_file() or path.stat().st_size < 1000:
        raise RuntimeError("EMPTY_OR_CORRUPT_ENCODED_VIDEO")
    reader = cv2.VideoCapture(str(path))
    if not reader.isOpened():
        raise RuntimeError("OPENCV_CANNOT_READ_ENCODED_VIDEO")
    decoded = 0
    try:
        while True:
            good, frame = reader.read()
            if not good:
                break
            decoded += 1
            if frame is None or frame.shape[:2] != (180, 320):
                raise RuntimeError("WRONG_DECODED_VIDEO_SHAPE")
    finally:
        reader.release()
    if decoded != fps * frame_count_each * 3 // fps:
        raise RuntimeError(f"DECODED_FRAME_COUNT_MISMATCH {decoded}/90")
    return fps, decoded, cv2.__version__


def execute_provider(script: Path, video: Path, root: Path, ffprobe: Path):
    return checked_run(
        [
            sys.executable, str(script),
            "--video", str(video),
            "--cutpilot-root", str(root),
            "--ffprobe", str(ffprobe),
            "--interval", "0.25",
            "--threshold", "0.52",
        ], timeout=50
    )


def verify(ffprobe: Path, output: Path) -> dict:
    if not output.is_absolute() or output.exists() or not output.parent.is_dir():
        raise RuntimeError("MUST_CHOOSE_NEW_ABSOLUTE_OUTPUT")
    if not ffprobe.is_file():
        raise RuntimeError("FFPROBE_BINARY_MISSING")
    try:
        import cv2
    except ImportError as exc:
        raise RuntimeError("OPENCV_NOT_INSTALLED") from exc
    provider = Path(__file__).resolve().with_name("cutpilot_foley_scene_provider.py")
    if not provider.is_file():
        raise RuntimeError("PROVIDER_SOURCE_MISSING")

    with tempfile.TemporaryDirectory(prefix="steinberg-cutpilot-real-") as directory:
        root = Path(directory)
        download_exact_scenes(root / "cutpilot" / "scenes.py")
        movie = root / "actual_decoded_fixture.mp4"
        fps, decoded, cv_version = create_encoded_video(movie)
        probe = checked_run([
            str(ffprobe), "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=avg_frame_rate,r_frame_rate,nb_frames",
            "-of", "json", str(movie)
        ])
        source_streams = json.loads(probe.stdout).get("streams", [])
        if len(source_streams) != 1:
            raise RuntimeError("FFPROBE_VIDEO_STREAM_INVALID")
        stream = source_streams[0]
        if stream.get("avg_frame_rate") != "24/1" or stream.get("nb_frames") != "90":
            raise RuntimeError("FFPROBE_CFR_EVIDENCE_INVALID")

        ran = execute_provider(provider, movie, root, ffprobe)
        document = json.loads(ran.stdout)
        if (document.get("provider") != "cutpilot.scenes.detect_scene_cuts" or
            document.get("detection_type") != "visual_histogram_scene_boundary_only" or
            document.get("automatic_action_recognition") is not False or
            document.get("human_review_required") is not True or
            document.get("fps_numerator") != 24 or
            document.get("fps_denominator") != 1 or
            document.get("video_frame_count") != 90):
            raise RuntimeError("REAL_PROVIDER_METADATA_MISMATCH")

        cues = document.get("cues")
        if (not isinstance(cues, list) or len(cues) != 2 or
            [item.get("frame") for item in cues] != [30, 60]):
            raise RuntimeError(f"REAL_SCENE_CUT_FRAMES_MISMATCH: {cues!r}")
        if any(item.get("track") != "other" or item.get("duration_frames") != 0
               for item in cues):
            raise RuntimeError("FALSE_FOLEY_TRACK_OR_DURATION")

        # Actual external ffprobe must reject invalid MP4 bytes, rather than
        # returning a fabricated video rate.
        bad = root / "invalid.mp4"
        bad.write_bytes(b"not a video")
        rejected = subprocess.run(
            [sys.executable, str(provider), "--video", str(bad),
             "--cutpilot-root", str(root), "--ffprobe", str(ffprobe)],
            capture_output=True, text=True, timeout=30, check=False
        )
        if (rejected.returncode == 0 or
            "CUTPILOT_SCENE_PROVIDER_REJECTED" not in rejected.stderr):
            raise RuntimeError("INVALID_VIDEO_WAS_NOT_REJECTED")

        result = {
            "status": "PASS_REAL_OPENCV_CUTPILOT_DECODED_SYNTHETIC_MP4_ONLY",
            "cutpilot_source_commit": CUTPILOT_HEAD,
            "cutpilot_scenes_git_blob_verified": CUTPILOT_SCENES_BLOB,
            "opencv_version": cv_version,
            "ffprobe_executable": str(ffprobe),
            "real_encoded_mp4": True,
            "real_opencv_decoder": True,
            "real_cutpilot_detect_scene_cuts": True,
            "synthetic_fixture_not_real_camera_footage": True,
            "decoded_frames": decoded,
            "declared_fps": fps,
            "scene_cut_frames": [30, 60],
            "scene_cut_seconds": [1.25, 2.5],
            "invalid_mp4_rejected": True,
            "model_action_recognition": False,
            "real_world_scene_accuracy": "NOT_VERIFIED",
            "cubase_nuendo_import": "NOT_RUN",
            "github_actions": "NOT_USED",
        }
        output.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf8")
        return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    ffprobe = shutil.which("ffprobe")
    if ffprobe is None:
        print("ENVIRONMENT_BLOCKED: ffprobe executable not installed", file=sys.stderr)
        return 3
    try:
        evidence = verify(Path(ffprobe).resolve(), Path(args.output).resolve())
    except (RuntimeError, OSError, subprocess.SubprocessError, ValueError,
            json.JSONDecodeError, urllib.error.URLError) as exc:
        print("CUTPILOT_REAL_VIDEO_GATE_FAILED: " + str(exc), file=sys.stderr)
        return 2
    print("STEINBERG_REAL_CUTPILOT_SCENE_PASS " + json.dumps(evidence, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
