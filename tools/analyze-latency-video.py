#!/usr/bin/env python3
"""
WindowStream Latency Video Analyzer
====================================
Analyzes latency-clock screen recordings to measure end-to-end frame-level
latency between the host clock (source monitor, top of camera frame) and
the XR spatial panel (decoded view, bottom of camera frame).

Uses green-channel color segmentation and template matching — no neural
networks, no EasyOCR. Dependencies: opencv-python, numpy, Pillow.

The latency-clock HTML renders the frame counter in bright green (#0f0) on
black. This script isolates those green digits via adaptive thresholding:
a low-pass finds cluster locations through the glow, then each cluster
is independently tried at multiple high thresholds (200→120) to find the
level that best strips glow while preserving digit separation. Digit
recognition uses normalized cross-correlation against rendered font
templates.

Usage:
    python tools/analyze-latency-video.py <video_or_image>
    python tools/analyze-latency-video.py recording.mp4 --step 5 --output-csv results.csv
    python tools/analyze-latency-video.py frame.jpg  # single-frame mode
"""

from __future__ import annotations

import argparse
import csv
import os
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont


# Font search order matches latency-clock.html CSS:
#   'Cascadia Mono', 'Consolas', 'Menlo', monospace
_FONT_CANDIDATES = [
    r"C:\Windows\Fonts\CascadiaMono.ttf",
    r"C:\Windows\Fonts\consola.ttf",
    r"C:\Windows\Fonts\cour.ttf",  # Courier New fallback
]

# Template heights to generate (pixels). Green-mode digits are ~30-120px tall;
# white timestamp digits can be 140-200px. We generate templates across a wide
# range and let NCC pick the best scale.
_TEMPLATE_HEIGHTS = [20, 28, 36, 44, 52, 64, 80, 100, 120, 150, 180]

# Low green threshold: finds cluster locations (captures the glow halo)
_GREEN_THRESHOLD_LOW = 50
_GREEN_DOMINANCE_LOW = 15

# High green dominance requirement for all recognition thresholds
_GREEN_DOMINANCE_HIGH = 20

# Adaptive thresholds tried per cluster (high to low). Higher thresholds
# strip more glow but may lose dim pixels; lower thresholds preserve more
# but cause digit merging. We try high-to-low and pick the best result.
_ADAPTIVE_THRESHOLDS = [200, 180, 160, 140, 120]


def _find_font_path() -> Optional[str]:
    for candidate in _FONT_CANDIDATES:
        if os.path.exists(candidate):
            return candidate
    return None


def _render_digit(digit: str, font: ImageFont.FreeTypeFont, height: int) -> np.ndarray:
    """Render a single digit as a tight-cropped white-on-black image at the
    given target height."""
    canvas_size = height * 3
    image = Image.new("L", (canvas_size, canvas_size), 0)
    draw = ImageDraw.Draw(image)
    if hasattr(font, "getbbox"):
        bounding_box = font.getbbox(digit)
        glyph_width = bounding_box[2] - bounding_box[0]
        glyph_height = bounding_box[3] - bounding_box[1]
    else:
        glyph_width, glyph_height = draw.textsize(digit, font=font)
    x_position = (canvas_size - glyph_width) // 2
    y_position = (canvas_size - glyph_height) // 2
    draw.text((x_position, y_position), digit, font=font, fill=255)

    array = np.array(image)
    coordinates = np.argwhere(array > 0)
    if coordinates.size == 0:
        return np.zeros((height, height // 2), dtype=np.uint8)
    y_min, x_min = coordinates.min(axis=0)
    y_max, x_max = coordinates.max(axis=0) + 1
    cropped = array[y_min:y_max, x_min:x_max]

    scale = height / cropped.shape[0]
    target_width = max(1, int(cropped.shape[1] * scale))
    resized = cv2.resize(cropped, (target_width, height), interpolation=cv2.INTER_AREA)
    _, binarized = cv2.threshold(resized, 80, 255, cv2.THRESH_BINARY)
    return binarized


@dataclass
class DigitTemplates:
    """Pre-rendered digit templates at multiple scales."""

    templates: dict[int, dict[str, np.ndarray]] = field(default_factory=dict)
    heights: list[int] = field(default_factory=list)

    @classmethod
    def generate(cls) -> "DigitTemplates":
        font_path = _find_font_path()
        if font_path is None:
            print(
                "Warning: No monospace font found; using Pillow default.",
                file=sys.stderr,
            )

        result = cls()
        result.heights = list(_TEMPLATE_HEIGHTS)

        for height in _TEMPLATE_HEIGHTS:
            font_size = int(height * 1.4)
            if font_path:
                try:
                    font = ImageFont.truetype(font_path, font_size)
                except IOError:
                    font = ImageFont.load_default()
            else:
                font = ImageFont.load_default()

            result.templates[height] = {}
            for digit in "0123456789":
                result.templates[height][digit] = _render_digit(digit, font, height)

        return result


def _build_green_mask(
    frame: np.ndarray,
    threshold: int,
    dominance: int,
) -> np.ndarray:
    """Isolate green pixels with configurable thresholds."""
    blue, green, red = cv2.split(frame)
    green_int = green.astype(np.int16)
    red_int = red.astype(np.int16)
    blue_int = blue.astype(np.int16)

    mask = (
        (green > threshold)
        & ((green_int - red_int) > dominance)
        & ((green_int - blue_int) > dominance)
    ).astype(np.uint8) * 255

    kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (3, 3))
    mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel)
    return mask


@dataclass
class DigitCluster:
    """A group of green digit pixels (one frame counter)."""

    x: int
    y: int
    width: int
    height: int
    label: str = ""  # "source" or "decoded"


def _find_digit_clusters(
    green_mask_low: np.ndarray,
    frame_height: int,
) -> list[DigitCluster]:
    """Find the two frame-counter digit clusters using the low-threshold mask.

    Identifies clusters by grouping nearby contours by vertical proximity,
    then picking the two largest clusters that look like digit groups.
    """
    contours, _ = cv2.findContours(
        green_mask_low,
        cv2.RETR_EXTERNAL,
        cv2.CHAIN_APPROX_SIMPLE,
    )

    # Collect bounding rects of meaningful contours.
    # Filter aggressively: area >= 200 excludes noise specks (camera sensor
    # hot pixels, compression artifacts) that inflate cluster bounding boxes.
    rectangles: list[tuple[int, int, int, int, float]] = []
    for contour in contours:
        x, y, width, height = cv2.boundingRect(contour)
        area = cv2.contourArea(contour)
        if height >= 12 and area >= 200:
            rectangles.append((x, y, width, height, area))

    if not rectangles:
        return []

    # Sort by y-center for grouping
    rectangles.sort(key=lambda rectangle: rectangle[1] + rectangle[3] // 2)

    # Group contours into clusters by vertical proximity
    clusters_raw: list[list[tuple[int, int, int, int, float]]] = []
    current_cluster: list[tuple[int, int, int, int, float]] = [rectangles[0]]

    for rectangle in rectangles[1:]:
        previous = current_cluster[-1]
        previous_y_center = previous[1] + previous[3] // 2
        current_y_center = rectangle[1] + rectangle[3] // 2
        # Group if vertically close (within 2× the height of the taller contour)
        max_gap = max(60, max(previous[3], rectangle[3]) * 2)
        if abs(current_y_center - previous_y_center) < max_gap:
            current_cluster.append(rectangle)
        else:
            clusters_raw.append(current_cluster)
            current_cluster = [rectangle]
    clusters_raw.append(current_cluster)

    scored_clusters: list[tuple[DigitCluster, float]] = []
    for group in clusters_raw:
        x_min = min(r[0] for r in group)
        y_min = min(r[1] for r in group)
        x_max = max(r[0] + r[2] for r in group)
        y_max = max(r[1] + r[3] for r in group)
        total_area = sum(r[4] for r in group)

        width = x_max - x_min
        height = y_max - y_min
        if width < 15 or height < 8:
            continue

        cluster = DigitCluster(
            x=x_min,
            y=y_min,
            width=width,
            height=height,
        )
        scored_clusters.append((cluster, total_area))

    # Sort by total area (largest first) and take top 2
    scored_clusters.sort(key=lambda pair: pair[1], reverse=True)
    top_clusters = [cluster for cluster, _ in scored_clusters[:2]]

    # Sort by y-position: top = source (physical monitor), bottom = decoded
    # (XR spatial panel). The HMD camera sees the monitor above the virtual panel.
    top_clusters.sort(key=lambda cluster: cluster.y)

    if len(top_clusters) >= 2:
        top_clusters[0].label = "source"
        top_clusters[1].label = "decoded"
        return top_clusters[:2]
    elif len(top_clusters) == 1:
        if top_clusters[0].y < frame_height // 2:
            top_clusters[0].label = "source"
        else:
            top_clusters[0].label = "decoded"
        return top_clusters
    return []


def _split_digits_by_projection(
    mask: np.ndarray,
) -> list[tuple[int, int]]:
    """Split a binary digit-group mask into individual digit x-ranges
    using vertical projection (column pixel counts).

    Gap-discovery-first: finds all significant gaps in the vertical
    projection, then derives digit count from gap count. No pre-estimated
    count needed.

    Returns list of (x_start, x_end) tuples, left-to-right.
    Falls back to equal-width slicing if no gaps are found.
    """
    if mask.size == 0:
        return []

    height, width = mask.shape[:2]
    column_sums = np.sum(mask, axis=0).astype(np.float64) / 255.0

    # Find active region
    active_columns = np.where(column_sums > 0)[0]
    if len(active_columns) == 0:
        return []

    first_active = int(active_columns[0])
    last_active = int(active_columns[-1])
    active_width = last_active - first_active + 1

    # Discover all gaps using a threshold relative to the max column density.
    # Columns with < 12% of the max pixel count are treated as gaps.
    max_col_sum = column_sums[first_active : last_active + 1].max()
    gap_threshold = max(1.0, max_col_sum * 0.12)

    gaps: list[tuple[int, int, int]] = []  # (start, end_inclusive, width)
    in_gap = False
    gap_start = 0

    for column_index in range(first_active, last_active + 1):
        if column_sums[column_index] < gap_threshold and not in_gap:
            in_gap = True
            gap_start = column_index
        elif column_sums[column_index] >= gap_threshold and in_gap:
            in_gap = False
            gap_width = column_index - gap_start
            # Require >= 2px wide and not touching the active region edges
            # (edge "gaps" are crop artifacts, not inter-digit spaces)
            if gap_width >= 2 and gap_start > first_active:
                gaps.append((gap_start, column_index - 1, gap_width))

    if gaps:
        # Use all discovered gaps to define digit boundaries.
        # Filter out very narrow gaps (< 40% of median gap width) that are
        # likely internal to a digit (e.g. the opening of a '4' or '6').
        gap_widths = sorted(g[2] for g in gaps)
        median_gap_width = gap_widths[len(gap_widths) // 2]
        minimum_gap_width = max(2, int(median_gap_width * 0.4))
        significant_gaps = [
            (start, end) for start, end, width in gaps if width >= minimum_gap_width
        ]

        if significant_gaps:
            digit_ranges: list[tuple[int, int]] = []
            previous_end = first_active
            for gap_start_column, gap_end_column in significant_gaps:
                digit_ranges.append((previous_end, gap_start_column))
                previous_end = gap_end_column + 1
            digit_ranges.append((previous_end, last_active + 1))

            # Sanity check: digit count should be 1-6
            if 1 <= len(digit_ranges) <= 6:
                return digit_ranges

    # Fallback: estimate digit count from aspect ratio and use equal slicing.
    # Monospace digit aspect ~0.6:1 width:height, with spacing ~0.75:1.
    aspect = active_width / max(1, height)
    estimated_count = max(1, min(6, round(aspect / 0.75)))
    digit_width = active_width / estimated_count
    return [
        (first_active + int(i * digit_width), first_active + int((i + 1) * digit_width))
        for i in range(estimated_count)
    ]


def _recognize_digit(
    digit_image: np.ndarray,
    templates: DigitTemplates,
) -> tuple[str, float]:
    """Recognize a single digit from a binary mask crop.

    Returns (digit_char, confidence) where confidence is the NCC score [0, 1].
    """
    # Tight-crop to active pixels
    coordinates = np.argwhere(digit_image > 0)
    if coordinates.size == 0:
        return ("?", 0.0)
    y_min, x_min = coordinates.min(axis=0)
    y_max, x_max = coordinates.max(axis=0) + 1
    cropped = digit_image[y_min:y_max, x_min:x_max]

    if cropped.shape[0] < 3 or cropped.shape[1] < 2:
        return ("?", 0.0)

    best_digit = "?"
    best_score = -1.0

    for height in templates.heights:
        # Resize input to match template height
        scale = height / cropped.shape[0]
        target_width = max(1, int(cropped.shape[1] * scale))
        resized = cv2.resize(
            cropped, (target_width, height), interpolation=cv2.INTER_AREA
        )
        _, binarized = cv2.threshold(resized, 80, 255, cv2.THRESH_BINARY)

        for digit_char, template in templates.templates[height].items():
            template_height, template_width = template.shape[:2]
            compare_height = max(template_height, binarized.shape[0])
            compare_width = max(template_width, binarized.shape[1])

            # Pad both to same size, centered
            padded_input = np.zeros((compare_height, compare_width), dtype=np.uint8)
            padded_template = np.zeros((compare_height, compare_width), dtype=np.uint8)

            input_y = (compare_height - binarized.shape[0]) // 2
            input_x = (compare_width - binarized.shape[1]) // 2
            padded_input[
                input_y : input_y + binarized.shape[0],
                input_x : input_x + binarized.shape[1],
            ] = binarized

            template_y = (compare_height - template_height) // 2
            template_x = (compare_width - template_width) // 2
            padded_template[
                template_y : template_y + template_height,
                template_x : template_x + template_width,
            ] = template

            # Normalized cross-correlation
            input_float = padded_input.astype(np.float32)
            template_float = padded_template.astype(np.float32)

            input_norm = np.linalg.norm(input_float)
            template_norm = np.linalg.norm(template_float)
            if input_norm < 1e-6 or template_norm < 1e-6:
                continue
            score = float(
                np.sum(input_float * template_float) / (input_norm * template_norm)
            )

            if score > best_score:
                best_score = score
                best_digit = digit_char

    return (best_digit, best_score)


def _try_recognize_at_threshold(
    cluster: DigitCluster,
    frame: np.ndarray,
    threshold: int,
    templates: DigitTemplates,
) -> tuple[Optional[int], float, np.ndarray]:
    """Try to recognize digits within a cluster at a specific green threshold.

    Returns (frame_number, avg_confidence, cropped_mask).
    """
    mask_full = _build_green_mask(frame, threshold, _GREEN_DOMINANCE_HIGH)

    # Extract mask within cluster bounds (with padding for threshold drift)
    padding = 15
    y_start = max(0, cluster.y - padding)
    y_end = min(mask_full.shape[0], cluster.y + cluster.height + padding)
    x_start = max(0, cluster.x - padding)
    x_end = min(mask_full.shape[1], cluster.x + cluster.width + padding)

    mask = mask_full[y_start:y_end, x_start:x_end].copy()

    # Tight-crop to active pixels
    active_pixels = np.argwhere(mask > 0)
    if active_pixels.size == 0:
        return (None, 0.0, np.array([]))

    y_min, x_min = active_pixels.min(axis=0)
    y_max, x_max = active_pixels.max(axis=0) + 1
    mask = mask[y_min:y_max, x_min:x_max]

    if mask.shape[0] < 5 or mask.shape[1] < 5:
        return (None, 0.0, mask)

    # Split digits via vertical projection
    digit_ranges = _split_digits_by_projection(mask)

    # Recognize each digit slice
    digits: list[str] = []
    confidences: list[float] = []

    for x_start_digit, x_end_digit in digit_ranges:
        digit_crop = mask[:, x_start_digit:x_end_digit]
        digit_char, confidence = _recognize_digit(digit_crop, templates)
        digits.append(digit_char)
        confidences.append(confidence)

    if not digits or all(d == "?" for d in digits):
        return (None, 0.0, mask)

    # Reject digits below confidence threshold
    digit_string = "".join(d if c > 0.35 else "?" for d, c in zip(digits, confidences))
    if "?" in digit_string:
        return (None, 0.0, mask)

    try:
        frame_number = int(digit_string)
        average_confidence = sum(confidences) / len(confidences)
        return (frame_number, average_confidence, mask)
    except ValueError:
        return (None, 0.0, mask)


def _recognize_cluster(
    cluster: DigitCluster,
    frame: np.ndarray,
    templates: DigitTemplates,
    debug_directory: Optional[Path] = None,
    frame_index: int = 0,
) -> tuple[Optional[int], float]:
    """Recognize the frame number from a digit cluster.

    Tries multiple green thresholds adaptively (200→120). The decoded panel
    (perspective-warped, glow-blurred from the camera) typically needs
    higher thresholds than the crisp source panel. We pick the result with
    the highest average confidence.
    """
    best_number: Optional[int] = None
    best_confidence: float = 0.0
    best_mask: Optional[np.ndarray] = None

    for threshold in _ADAPTIVE_THRESHOLDS:
        frame_number, confidence, mask = _try_recognize_at_threshold(
            cluster,
            frame,
            threshold,
            templates,
        )
        if frame_number is not None and confidence > best_confidence:
            best_number = frame_number
            best_confidence = confidence
            best_mask = mask

    if debug_directory is not None and best_mask is not None and best_mask.size > 0:
        debug_path = (
            debug_directory / f"frame_{frame_index:06d}_{cluster.label}_mask_best.png"
        )
        cv2.imwrite(str(debug_path), best_mask)

    return (best_number, best_confidence)


_WHITE_THRESHOLDS = [240, 220, 200, 180, 160]


def _build_white_mask(frame: np.ndarray, threshold: int) -> np.ndarray:
    """Build a binary mask of bright (white) pixels."""
    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
    _, mask = cv2.threshold(gray, threshold, 255, cv2.THRESH_BINARY)
    kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (3, 3))
    mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel)
    return mask


def _deduplicate_digit_rects(
    rects: list[tuple[int, int, int, int, int]],
) -> list[tuple[int, int, int, int, int]]:
    """Remove overlapping bounding rects (e.g. inner/outer of '0', '6', '8').

    For rects with >50% horizontal overlap, keeps the one with larger area.
    """
    if not rects:
        return []
    sorted_rects = sorted(rects, key=lambda r: r[0])
    result = [sorted_rects[0]]
    for rect in sorted_rects[1:]:
        x, _y, w, _h, a = rect
        previous_x, _, previous_w, _, previous_a = result[-1]
        x_overlap = max(
            0,
            min(x + w, previous_x + previous_w) - max(x, previous_x),
        )
        if x_overlap > min(w, previous_w) * 0.5:
            # Overlapping — keep the one with larger area
            if a > previous_a:
                result[-1] = rect
        else:
            result.append(rect)
    return result


def _find_digit_row_in_mask(
    mask: np.ndarray,
) -> list[tuple[int, int, int, int, int]]:
    """Find the best row of digit-sized contours in a white mask.

    Uses RETR_LIST (not RETR_EXTERNAL) to find contours inside larger
    bright regions (e.g. digits inside a monitor bezel area).
    Filters by size and aspect ratio, deduplicates overlaps, then
    picks the y-group closest to 9 contours (HH:MM:SS.mmm = 9 digits).

    Returns list of (x, y, w, h, area) sorted left-to-right.
    """
    contours, _ = cv2.findContours(
        mask,
        cv2.RETR_LIST,
        cv2.CHAIN_APPROX_SIMPLE,
    )

    # Filter to digit-like contours: portrait orientation, reasonable size
    digit_rects: list[tuple[int, int, int, int, int]] = []
    for contour in contours:
        x, y, w, h = cv2.boundingRect(contour)
        area = cv2.contourArea(contour)
        if h >= 40 and w >= 10 and area >= 300:
            aspect = w / h
            if 0.15 <= aspect <= 0.90:
                digit_rects.append((x, y, w, h, area))

    if not digit_rects:
        return []

    digit_rects = _deduplicate_digit_rects(digit_rects)

    # Group by y-center proximity
    digit_rects.sort(key=lambda r: r[1] + r[3] // 2)
    groups: list[list[tuple[int, int, int, int, int]]] = [[digit_rects[0]]]
    for rect in digit_rects[1:]:
        previous = groups[-1][-1]
        previous_y_center = previous[1] + previous[3] // 2
        current_y_center = rect[1] + rect[3] // 2
        if abs(current_y_center - previous_y_center) < max(previous[3], rect[3]):
            groups[-1].append(rect)
        else:
            groups.append([rect])

    # Pick the group closest to 9 digits (HH:MM:SS.mmm)
    best_group: Optional[list[tuple[int, int, int, int, int]]] = None
    best_difference = float("inf")
    for group in groups:
        difference = abs(len(group) - 9)
        if difference < best_difference:
            best_difference = difference
            best_group = group

    if best_group is None:
        return []
    return sorted(best_group, key=lambda r: r[0])


def _parse_timestamp_to_milliseconds(digits: list[str]) -> Optional[int]:
    """Parse 9 digits (HHMMSS mmm) into milliseconds since midnight."""
    if len(digits) != 9:
        return None
    try:
        hours = int(digits[0] + digits[1])
        minutes = int(digits[2] + digits[3])
        seconds = int(digits[4] + digits[5])
        millis = int(digits[6] + digits[7] + digits[8])
    except ValueError:
        return None
    if hours > 23 or minutes > 59 or seconds > 59 or millis > 999:
        return None
    return hours * 3600000 + minutes * 60000 + seconds * 1000 + millis


def _milliseconds_to_timestamp_string(milliseconds: int) -> str:
    """Format milliseconds since midnight as HH:MM:SS.mmm."""
    hours = milliseconds // 3600000
    remainder = milliseconds % 3600000
    minutes = remainder // 60000
    remainder = remainder % 60000
    seconds = remainder // 1000
    millis = remainder % 1000
    return f"{hours:02d}:{minutes:02d}:{seconds:02d}.{millis:03d}"


def _recognize_timestamp_in_region(
    frame_region: np.ndarray,
    templates: DigitTemplates,
) -> tuple[Optional[int], float]:
    """Recognize a wall-clock timestamp (HH:MM:SS.mmm) in a frame region.

    Tries multiple white thresholds adaptively, picking the one that
    produces exactly 9 digit contours with highest recognition confidence.

    Returns (milliseconds_since_midnight, avg_confidence) or (None, 0.0).
    """
    best_milliseconds: Optional[int] = None
    best_confidence: float = 0.0

    for threshold in _WHITE_THRESHOLDS:
        mask = _build_white_mask(frame_region, threshold)
        digit_rects = _find_digit_row_in_mask(mask)

        if len(digit_rects) != 9:
            continue

        # Recognize each digit
        digits: list[str] = []
        confidences: list[float] = []
        for x, y, w, h, _ in digit_rects:
            crop = mask[y : y + h, x : x + w]
            digit_char, confidence = _recognize_digit(crop, templates)
            digits.append(digit_char)
            confidences.append(confidence)

        # Lower threshold than green mode: the timestamp structure
        # (valid HH:MM:SS.mmm) provides strong validation that frame
        # numbers lack, so we can tolerate lower per-digit confidence.
        digit_string = "".join(
            d if c > 0.15 else "?" for d, c in zip(digits, confidences)
        )
        if "?" in digit_string:
            continue

        milliseconds = _parse_timestamp_to_milliseconds(list(digit_string))
        if milliseconds is None:
            continue

        average_confidence = sum(confidences) / len(confidences)
        if average_confidence > best_confidence:
            best_milliseconds = milliseconds
            best_confidence = average_confidence

    return (best_milliseconds, best_confidence)


@dataclass
class FrameResult:
    """Result of analyzing one video frame."""

    video_frame_index: int
    timestamp_seconds: float
    source_frame: Optional[int] = None
    source_confidence: float = 0.0
    decoded_frame: Optional[int] = None
    decoded_confidence: float = 0.0
    latency_frames: Optional[int] = None
    latency_milliseconds: Optional[float] = None
    format_mode: str = "green"  # "green" or "timestamp"


def analyze_frame(
    frame: np.ndarray,
    templates: DigitTemplates,
    video_frame_index: int,
    timestamp_seconds: float,
    clock_rate: float,
    debug_directory: Optional[Path] = None,
) -> FrameResult:
    """Analyze a single video frame for latency measurement.

    Auto-detects format:
    1. Green mode (newer layout): bright green frame counters
    2. White timestamp mode (older layout): white HH:MM:SS.mmm clocks
    """
    result = FrameResult(
        video_frame_index=video_frame_index,
        timestamp_seconds=timestamp_seconds,
    )

    green_mask_low = _build_green_mask(
        frame,
        _GREEN_THRESHOLD_LOW,
        _GREEN_DOMINANCE_LOW,
    )

    frame_height = frame.shape[0]

    if debug_directory is not None:
        cv2.imwrite(
            str(debug_directory / f"frame_{video_frame_index:06d}_mask_low.png"),
            green_mask_low,
        )

    clusters = _find_digit_clusters(green_mask_low, frame_height)

    if len(clusters) >= 2:
        # Green mode: recognize frame counters
        result.format_mode = "green"
        for cluster in clusters:
            frame_number, confidence = _recognize_cluster(
                cluster,
                frame,
                templates,
                debug_directory,
                video_frame_index,
            )
            if cluster.label == "source":
                result.source_frame = frame_number
                result.source_confidence = confidence
            elif cluster.label == "decoded":
                result.decoded_frame = frame_number
                result.decoded_confidence = confidence

        if result.source_frame is not None and result.decoded_frame is not None:
            result.latency_frames = result.source_frame - result.decoded_frame
            result.latency_milliseconds = result.latency_frames * (1000.0 / clock_rate)

    # Fall back to white timestamp mode if green mode didn't produce a pair.
    # This handles both "no green clusters found" and "green clusters found
    # but they were FRAME: text, not digit-only counters".
    if result.source_frame is None or result.decoded_frame is None:
        half = frame_height // 2
        source_region = frame[:half, :]
        decoded_region = frame[half:, :]

        source_milliseconds, source_confidence = _recognize_timestamp_in_region(
            source_region, templates
        )
        decoded_milliseconds, decoded_confidence = _recognize_timestamp_in_region(
            decoded_region, templates
        )

        if source_milliseconds is not None or decoded_milliseconds is not None:
            # White timestamp mode succeeded — override any partial green results
            result.format_mode = "timestamp"
            result.source_frame = source_milliseconds
            result.source_confidence = source_confidence
            result.decoded_frame = decoded_milliseconds
            result.decoded_confidence = decoded_confidence
            result.latency_frames = None  # not applicable

            if source_milliseconds is not None and decoded_milliseconds is not None:
                result.latency_milliseconds = float(
                    source_milliseconds - decoded_milliseconds
                )

    return result


def process_video(
    video_path: Path,
    templates: DigitTemplates,
    step: int,
    clock_rate: float,
    skip_seconds: float = 0.0,
    debug_directory: Optional[Path] = None,
) -> list[FrameResult]:
    """Process all sampled frames from a video file."""
    capture = cv2.VideoCapture(str(video_path))
    if not capture.isOpened():
        print(f"Error: cannot open {video_path}", file=sys.stderr)
        return []

    total_frames = int(capture.get(cv2.CAP_PROP_FRAME_COUNT))
    frames_per_second = capture.get(cv2.CAP_PROP_FPS) or 30.0
    duration = total_frames / frames_per_second

    frames_to_process = total_frames // step
    print(f"Video: {video_path.name}")
    print(f"  {total_frames} frames, {frames_per_second:.1f} FPS, {duration:.1f}s")
    print(f"  Sampling every {step}th frame -> {frames_to_process} frames to analyze")
    print()

    results: list[FrameResult] = []
    frame_index = 0
    processed = 0

    while capture.isOpened():
        success, frame = capture.read()
        if not success:
            break

        if frame_index % step == 0:
            timestamp = frame_index / frames_per_second
            if timestamp < skip_seconds:
                frame_index += 1
                continue
            frame_result = analyze_frame(
                frame,
                templates,
                frame_index,
                timestamp,
                clock_rate,
                debug_directory,
            )
            results.append(frame_result)
            processed += 1

            # Progress indicator every 20 frames
            if processed % 20 == 0:
                print(
                    f"  [{processed}/{frames_to_process}] "
                    f"t={timestamp:.1f}s "
                    f"src={frame_result.source_frame} "
                    f"dec={frame_result.decoded_frame} "
                    f"lat={frame_result.latency_frames}"
                )

        frame_index += 1

    capture.release()
    print(f"  Done: {processed} frames analyzed")
    return results


def process_image(
    image_path: Path,
    templates: DigitTemplates,
    clock_rate: float,
    debug_directory: Optional[Path] = None,
) -> list[FrameResult]:
    """Process a single image file."""
    frame = cv2.imread(str(image_path))
    if frame is None:
        print(f"Error: cannot read {image_path}", file=sys.stderr)
        return []

    print(f"Image: {image_path.name} ({frame.shape[1]}x{frame.shape[0]})")
    result = analyze_frame(frame, templates, 0, 0.0, clock_rate, debug_directory)
    return [result]


def _compute_frozen_indices(valid_pairs: list[FrameResult]) -> set[int]:
    """Detect frozen runs: decoded_frame identical across 3+ consecutive samples
    means the stream wasn't flowing yet (or froze)."""
    frozen_indices: set[int] = set()
    if len(valid_pairs) < 3:
        return frozen_indices
    run_start = 0
    for i in range(1, len(valid_pairs)):
        if valid_pairs[i].decoded_frame != valid_pairs[run_start].decoded_frame:
            if i - run_start >= 3:
                for j in range(run_start, i):
                    frozen_indices.add(valid_pairs[j].video_frame_index)
            run_start = i
    if len(valid_pairs) - run_start >= 3:
        for j in range(run_start, len(valid_pairs)):
            frozen_indices.add(valid_pairs[j].video_frame_index)
    return frozen_indices


def _filter_plausible(
    valid_pairs: list[FrameResult],
    is_timestamp_mode: bool,
    frozen_indices: set[int],
) -> list[FrameResult]:
    if is_timestamp_mode:
        return [
            r
            for r in valid_pairs
            if r.latency_milliseconds is not None
            and 0 <= r.latency_milliseconds <= 2000
            and r.video_frame_index not in frozen_indices
        ]
    return [
        r
        for r in valid_pairs
        if r.latency_frames is not None
        and 0 <= r.latency_frames <= 200
        and r.video_frame_index not in frozen_indices
    ]


@dataclass
class _QualityCounts:
    total: int
    source_recognized: int
    decoded_recognized: int
    valid_pairs: int
    frozen: int
    plausible: int


def _compute_quality_counts(
    results: list[FrameResult],
    valid_pairs: list[FrameResult],
    frozen_count: int,
    plausible: list[FrameResult],
) -> _QualityCounts:
    return _QualityCounts(
        total=len(results),
        source_recognized=sum(1 for r in results if r.source_frame is not None),
        decoded_recognized=sum(1 for r in results if r.decoded_frame is not None),
        valid_pairs=len(valid_pairs),
        frozen=frozen_count,
        plausible=len(plausible),
    )


def _print_quality_summary(
    input_path: Path,
    clock_rate: float,
    is_timestamp_mode: bool,
    counts: _QualityCounts,
) -> None:
    print()
    print("WindowStream Latency Analysis")
    print("=" * 50)
    print(f"Input:           {input_path.name}")
    print(f"Frames analyzed: {counts.total}")
    if is_timestamp_mode:
        print("Format:          White timestamps (HH:MM:SS.mmm)")
    else:
        print(
            f"Clock rate:      {clock_rate:.1f} FPS "
            f"({1000.0 / clock_rate:.2f} ms/frame)"
        )
    print()

    print("OCR Quality:")
    if counts.total > 0:
        print(
            f"  Source (top):    {counts.source_recognized}/{counts.total} recognized "
            f"({100.0 * counts.source_recognized / counts.total:.1f}%)"
        )
        print(
            f"  Decoded (bottom): {counts.decoded_recognized}/{counts.total} recognized "
            f"({100.0 * counts.decoded_recognized / counts.total:.1f}%)"
        )
        print(
            f"  Valid pairs:     {counts.valid_pairs}/{counts.total} "
            f"({100.0 * counts.valid_pairs / counts.total:.1f}%)"
        )
    if counts.frozen > 0:
        print(
            f"  Frozen:          {counts.frozen} frames excluded (stream not flowing)"
        )
    if counts.plausible < counts.valid_pairs - counts.frozen:
        outlier_count = counts.valid_pairs - counts.frozen - counts.plausible
        print(
            f"  Outliers:        {outlier_count} frames excluded (implausible latency)"
        )
    print()


def _print_unfiltered_pairs(
    valid_pairs: list[FrameResult], is_timestamp_mode: bool
) -> None:
    print("No valid latency measurements. Check OCR quality above.")
    if not valid_pairs:
        return
    print("\nAll valid pairs (unfiltered):")
    for result in valid_pairs[:20]:
        if is_timestamp_mode:
            source_string = (
                _milliseconds_to_timestamp_string(result.source_frame)
                if result.source_frame is not None
                else "-"
            )
            decoded_string = (
                _milliseconds_to_timestamp_string(result.decoded_frame)
                if result.decoded_frame is not None
                else "-"
            )
            print(
                f"  frame {result.video_frame_index}: "
                f"src={source_string} dec={decoded_string} "
                f"lat={result.latency_milliseconds:.1f} ms"
            )
        else:
            print(
                f"  frame {result.video_frame_index}: "
                f"src={result.source_frame} dec={result.decoded_frame} "
                f"lat={result.latency_frames}"
            )


def _print_latency_metrics(
    plausible: list[FrameResult],
    is_timestamp_mode: bool,
    latencies_milliseconds: np.ndarray,
) -> None:
    if not is_timestamp_mode:
        latencies_frames = np.array(
            [r.latency_frames for r in plausible if r.latency_frames is not None],
            dtype=np.float64,
        )
        if len(latencies_frames) > 0:
            print("Latency Metrics (frames):")
            print(f"  p0  (Min):       {np.min(latencies_frames):.0f}")
            print(f"  p50 (Median):    {np.median(latencies_frames):.0f}")
            print(f"  p95:             {np.percentile(latencies_frames, 95):.0f}")
            print(f"  Max:             {np.max(latencies_frames):.0f}")
            print(
                f"  Mean +/- Std:    {np.mean(latencies_frames):.1f} "
                f"+/- {np.std(latencies_frames):.1f}"
            )
            print()

    print("Latency Metrics (ms):")
    print(f"  p0  (Min):       {np.min(latencies_milliseconds):.1f} ms")
    print(f"  p50 (Median):    {np.median(latencies_milliseconds):.1f} ms")
    print(f"  p95:             {np.percentile(latencies_milliseconds, 95):.1f} ms")
    print(f"  Max:             {np.max(latencies_milliseconds):.1f} ms")
    print(
        f"  Mean +/- Std:    {np.mean(latencies_milliseconds):.1f} "
        f"+/- {np.std(latencies_milliseconds):.1f} ms"
    )
    print()


def _print_timeline_sample(
    plausible: list[FrameResult], is_timestamp_mode: bool
) -> None:
    sample_size = min(15, len(plausible))
    print(f"Sample Timeline (first {sample_size} valid frames):")
    if is_timestamp_mode:
        print(f"  {'Time':>8}  {'Source':>14}  {'Decoded':>14}  {'D ms':>10}")
        for result in plausible[:sample_size]:
            source_string = (
                _milliseconds_to_timestamp_string(result.source_frame)
                if result.source_frame is not None
                else "-"
            )
            decoded_string = (
                _milliseconds_to_timestamp_string(result.decoded_frame)
                if result.decoded_frame is not None
                else "-"
            )
            milliseconds_string = (
                f"{result.latency_milliseconds:.1f} ms"
                if result.latency_milliseconds is not None
                else "-"
            )
            print(
                f"  {result.timestamp_seconds:7.2f}s  {source_string:>14}  "
                f"{decoded_string:>14}  {milliseconds_string:>10}"
            )
    else:
        print(
            f"  {'Time':>8}  {'Source':>8}  {'Decoded':>8}  "
            f"{'D frames':>8}  {'D ms':>10}"
        )
        for result in plausible[:sample_size]:
            source_string = (
                str(result.source_frame) if result.source_frame is not None else "-"
            )
            decoded_string = (
                str(result.decoded_frame) if result.decoded_frame is not None else "-"
            )
            latency_string = (
                str(result.latency_frames) if result.latency_frames is not None else "-"
            )
            milliseconds_string = (
                f"{result.latency_milliseconds:.1f} ms"
                if result.latency_milliseconds is not None
                else "-"
            )
            print(
                f"  {result.timestamp_seconds:7.2f}s  {source_string:>8}  "
                f"{decoded_string:>8}  {latency_string:>8}  "
                f"{milliseconds_string:>10}"
            )


def generate_report(
    results: list[FrameResult],
    input_path: Path,
    clock_rate: float,
) -> None:
    """Print the latency analysis report to stdout."""
    # Detect mode from the first result that has data
    is_timestamp_mode = any(r.format_mode == "timestamp" for r in results)

    # Valid pairs: have a latency measurement (frames or milliseconds)
    valid_pairs = [r for r in results if r.latency_milliseconds is not None]

    frozen_indices = _compute_frozen_indices(valid_pairs)
    frozen_count = sum(1 for r in valid_pairs if r.video_frame_index in frozen_indices)
    plausible = _filter_plausible(valid_pairs, is_timestamp_mode, frozen_indices)

    counts = _compute_quality_counts(results, valid_pairs, frozen_count, plausible)
    _print_quality_summary(input_path, clock_rate, is_timestamp_mode, counts)

    if not plausible:
        _print_unfiltered_pairs(valid_pairs, is_timestamp_mode)
        return

    latencies_milliseconds = np.array(
        [
            r.latency_milliseconds
            for r in plausible
            if r.latency_milliseconds is not None
        ],
        dtype=np.float64,
    )

    if len(latencies_milliseconds) == 0:
        print("No valid latency measurements after filtering.")
        return

    _print_latency_metrics(plausible, is_timestamp_mode, latencies_milliseconds)
    _print_timeline_sample(plausible, is_timestamp_mode)


def write_csv(results: list[FrameResult], output_path: Path) -> None:
    """Write results to CSV."""
    with open(output_path, "w", newline="", encoding="utf-8") as csv_file:
        writer = csv.writer(csv_file)
        writer.writerow(
            [
                "video_frame",
                "timestamp_sec",
                "source_frame",
                "source_confidence",
                "decoded_frame",
                "decoded_confidence",
                "latency_frames",
                "latency_ms",
            ]
        )
        for result in results:
            writer.writerow(
                [
                    result.video_frame_index,
                    f"{result.timestamp_seconds:.3f}",
                    result.source_frame if result.source_frame is not None else "",
                    f"{result.source_confidence:.3f}",
                    result.decoded_frame if result.decoded_frame is not None else "",
                    f"{result.decoded_confidence:.3f}",
                    result.latency_frames if result.latency_frames is not None else "",
                    (
                        f"{result.latency_milliseconds:.2f}"
                        if result.latency_milliseconds is not None
                        else ""
                    ),
                ]
            )
    print(f"\nCSV written: {output_path}")


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Analyze WindowStream latency-clock recordings.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument(
        "input",
        type=str,
        help="Path to a .mp4 video or a .jpg/.png image.",
    )
    parser.add_argument(
        "--step",
        type=int,
        default=5,
        help="Process every Nth video frame (default: 5, ignored for images).",
    )
    parser.add_argument(
        "--clock-rate",
        type=float,
        default=165.0,
        help=(
            "Frame-counter rate in FPS "
            "(default: 165.0, matches latency-clock.html ?cap=165)."
        ),
    )
    parser.add_argument(
        "--output-csv",
        type=str,
        default=None,
        help="Write per-frame results to this CSV file.",
    )
    parser.add_argument(
        "--skip",
        type=float,
        default=0.0,
        help="Skip the first N seconds of video (default: 0).",
    )
    parser.add_argument(
        "--debug-dir",
        type=str,
        default=None,
        help=(
            "Write intermediate debug images "
            "(green masks, digit crops) to this directory."
        ),
    )
    return parser.parse_args()


def main() -> int:
    arguments = parse_arguments()

    input_path = Path(arguments.input)
    if not input_path.exists():
        print(f"Error: {input_path} not found.", file=sys.stderr)
        return 1

    debug_directory = None
    if arguments.debug_dir:
        debug_directory = Path(arguments.debug_dir)
        debug_directory.mkdir(parents=True, exist_ok=True)

    print("Generating digit templates...")
    templates = DigitTemplates.generate()
    print(
        f"  {len(templates.heights)} scales x 10 digits "
        f"= {len(templates.heights) * 10} templates"
    )
    print()

    # Detect input type
    suffix = input_path.suffix.lower()
    if suffix in (".mp4", ".mkv", ".avi", ".webm", ".mov"):
        results = process_video(
            input_path,
            templates,
            arguments.step,
            arguments.clock_rate,
            arguments.skip,
            debug_directory,
        )
    elif suffix in (".jpg", ".jpeg", ".png", ".bmp", ".tiff"):
        results = process_image(
            input_path,
            templates,
            arguments.clock_rate,
            debug_directory,
        )
    else:
        print(f"Error: unrecognized file type '{suffix}'", file=sys.stderr)
        return 1

    if not results:
        print("No frames analyzed.", file=sys.stderr)
        return 1

    generate_report(results, input_path, arguments.clock_rate)

    if arguments.output_csv:
        write_csv(results, Path(arguments.output_csv))

    return 0


if __name__ == "__main__":
    sys.exit(main())
