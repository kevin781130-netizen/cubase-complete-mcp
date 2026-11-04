from __future__ import annotations

from pathlib import Path


def detect_scene_cuts(
    video_path: str | Path,
    start: float,
    end: float,
    *,
    sample_interval: float = 0.25,
    threshold: float = 0.52,
    min_gap: float = 0.65,
) -> list[float]:
    """Lightweight OpenCV histogram scene-cut detector.

    Returns absolute source-video timestamps. It is intentionally dependency-light
    and exists mainly to reset crop smoothing at hard edits.
    """
    try:
        import cv2
    except ImportError as exc:
        raise RuntimeError("opencv-python-headless is required for scene detection") from exc

    cap = cv2.VideoCapture(str(video_path))
    if not cap.isOpened():
        return []
    cuts: list[float] = []
    previous = None
    last_cut = start - min_gap
    try:
        t = max(0.0, start)
        while t <= end:
            cap.set(cv2.CAP_PROP_POS_MSEC, t * 1000.0)
            ok, frame = cap.read()
            if not ok:
                t += sample_interval
                continue
            small = cv2.resize(frame, (160, 90), interpolation=cv2.INTER_AREA)
            hsv = cv2.cvtColor(small, cv2.COLOR_BGR2HSV)
            hist = cv2.calcHist([hsv], [0, 1], None, [32, 32], [0, 180, 0, 256])
            cv2.normalize(hist, hist, 0, 1, cv2.NORM_MINMAX)
            if previous is not None:
                correlation = cv2.compareHist(previous, hist, cv2.HISTCMP_CORREL)
                distance = 1.0 - max(-1.0, min(1.0, correlation))
                if distance >= threshold and (t - last_cut) >= min_gap:
                    cuts.append(t)
                    last_cut = t
            previous = hist
            t += sample_interval
    finally:
        cap.release()
    return cuts
