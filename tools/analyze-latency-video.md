# Latency Video Analysis Pipeline

Automated measurement of end-to-end photon-to-photon latency from
camera recordings of the latency-clock HTML alongside the XR spatial panel.

## Quick Start

```bash
# Newer format (green frame counters)
python tools/analyze-latency-video.py recording.mp4 --step 5

# Older format (white HH:MM:SS.mmm timestamps) — auto-detected
python tools/analyze-latency-video.py recording.mp4 --step 10

# Single frame
python tools/analyze-latency-video.py frame.jpg

# Full options
python tools/analyze-latency-video.py recording.mp4 \
    --step 5 \
    --skip 3.0 \
    --clock-rate 165 \
    --output-csv results.csv \
    --debug-dir ./debug
```

| Flag | Default | Description |
| --- | --- | --- |
| `--step N` | 5 | Process every Nth video frame |
| `--skip S` | 0 | Skip the first S seconds of video |
| `--clock-rate F` | 165.0 | Frame-counter rate in FPS (green mode only) |
| `--output-csv` | — | Write per-frame CSV |
| `--debug-dir` | — | Write intermediate masks for troubleshooting |

## Supported Formats

The pipeline auto-detects which format is present in each frame.

### Green frame-counter mode (newer)

The latency-clock HTML renders a large green (#0f0) frame counter on black.
The camera films both the source monitor (top of frame) and the XR spatial
panel (bottom). The pipeline:

1. **Green-channel segmentation** (low threshold, g>50) finds digit cluster
   locations through the glow halo.
2. **Adaptive per-cluster thresholding** (200→120) strips glow independently
   per panel — the decoded panel needs higher thresholds to separate
   perspective-warped digits.
3. **Template matching** (NCC against Cascadia Mono / Consolas rendered at
   11 scales from 20–180px) recognizes each digit.
4. Latency = `(source_frame − decoded_frame) × ms_per_frame`.

### White timestamp mode (older)

The older latency-clock layout shows wall-clock timestamps (`HH:MM:SS.mmm`)
in large white text. The pipeline:

1. Splits the frame in half (top = source monitor, bottom = decoded panel).
2. **Brightness thresholding** at adaptive levels (240→160) isolates white
   digit contours.
3. **RETR_LIST contour detection** finds digits inside larger bright regions
   (the monitor bezel doesn't occlude them).
4. **Deduplication** removes inner/outer contour pairs from digits with holes
   (`0`, `6`, `8`, `9`).
5. Selects the y-group with exactly 9 contours (HH MM SS mmm).
6. Template matching recognizes each digit; timestamp structural validation
   (`hours ≤ 23`, `minutes ≤ 59`, etc.) catches misrecognitions.
7. Latency = `source_ms − decoded_ms` directly in milliseconds.

### Format detection logic

Green mode is tried first. If it finds ≥ 2 green digit clusters **and**
successfully recognizes both panels, it's used. Otherwise the pipeline
falls back to white timestamp mode. This handles the case where the older
format's green `FRAME: 5510 · RENDER: 165.0 FPS` text creates green
clusters that can't be parsed as digit-only counters.

## Filtering

### Frozen frame detection

If the decoded panel shows the same value for 3+ consecutive samples,
those frames are excluded as "frozen" (stream wasn't flowing). This
commonly happens in the first few seconds of a recording before the
capture pipeline starts delivering frames.

### Outlier filtering

- **Green mode**: latency must be in `[0, 200]` frames.
- **Timestamp mode**: latency must be in `[0, 2000]` ms.

Negative latency (decoded ahead of source) is physically impossible and
indicates an OCR misread. Values beyond the upper bound are implausible
for real-time streaming.

### Manual skip

`--skip N` drops the first N seconds of video before analysis begins.

## Baseline Results

Measured on Quest 3 / Galaxy XR, GPU-resident pipeline, May 2026.

### Green frame-counter recordings (20260526)

| Recording | Frames | Source % | Decoded % | p50 (frames) | p50 (ms) |
| --- | ---: | ---: | ---: | ---: | ---: |
| 014405.mp4 | 54 | 100% | 100% | 4 | 24.2 |
| 013458.mp4 | 54 | 88.9% | 100% | 5 | 30.3 |

### White timestamp recording (20260524)

| Recording | Frames | Source % | Decoded % | p50 (ms) |
| --- | ---: | ---: | ---: | ---: |
| 074503.mp4 | 54 | 94.4% | 100% | 24.0 |

**Consistent p50 of ~24 ms across both formats**, matching the known
software-level baseline (convert → present p50 = 28 ms from `[FRAMECOUNT]`
instrumentation).

## Dependencies

- `opencv-python` — contour detection, thresholding, video I/O
- `numpy` — array operations, statistics
- `Pillow` — font rendering for digit templates

No neural networks, no EasyOCR, no PyTorch.

Requires a monospace font for template generation (checked in order):
`CascadiaMono.ttf`, `consola.ttf`, `cour.ttf` from `C:\Windows\Fonts\`.

## Troubleshooting

**0% recognition on both panels**: Check `--debug-dir` output. If
`mask_low.png` shows no green regions, the frame uses the white timestamp
format. Verify the green→white fallback is firing (look for `format_mode`
in the CSV).

**Negative latency values**: The source/decoded panel labels are swapped.
In the camera frame, the physical source monitor is at the **top** and the
virtual decoded panel is at the **bottom**.

**Low recognition on source panel**: The source monitor is typically
further from the camera and more perspective-distorted. The decoded panel
(in-headset) is closer and sharper. This is expected — decoded recognition
is consistently higher than source.

**Frozen frames not filtered**: The auto-detection requires 3+ consecutive
identical decoded values. With `--step 20`, this means 60 video frames
(~2s at 30 FPS) of frozen content. Lower `--step` for better frozen
detection granularity.
