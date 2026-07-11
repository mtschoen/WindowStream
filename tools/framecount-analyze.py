"""
Join FRAMECOUNT logs from server (.NET stderr) and viewer (Android logcat),
report per-stage p50/p95 latencies.

Usage:
    python tools/framecount-analyze.py m5-final-server.log m5-final-viewer.log

Stages, in pipeline order:
    convert  (server, after WGC->NV12 GPU conversion)
    enc      (server, after NVENC packet emit)
    reasm    (viewer, after NAL reassembly)
    dec      (viewer, after MediaCodec output buffer rendered)
    present  (viewer, after Choreographer frame callback)

PTS (microseconds since capture start) is the join key across all five
stages. wallMs is Unix-epoch milliseconds; cross-source deltas
(server->viewer) include any clock skew between the two machines, which
we cannot eliminate without an in-band sync. Same-source deltas are
clock-skew-free.
"""

from __future__ import annotations

import re
import statistics
import sys
from dataclasses import dataclass
from pathlib import Path

LINE_PATTERN = re.compile(
    r"FRAMECOUNT[^a-z]*stage=(?P<stage>[a-z]+)\s+"
    r"ptsUs=(?P<pts>-?\d+)\s+wallMs=(?P<wall>\d+)"
)

SERVER_STAGES = ("convert", "enc")
VIEWER_STAGES = ("reasm", "dec", "present")
ALL_STAGES = SERVER_STAGES + VIEWER_STAGES

DELTAS = [
    ("convert", "enc", "convert -> enc      (server, GPU->NVENC)", False),
    ("enc", "reasm", "enc     -> reasm    (network + reassembly)", True),
    ("reasm", "dec", "reasm   -> dec      (viewer decode)", False),
    ("dec", "present", "dec     -> present  (viewer render)", False),
    ("convert", "present", "convert -> present  (END-TO-END)", True),
]


@dataclass
class Sample:
    stage: str
    pts_us: int
    wall_ms: int


def parse(path: Path) -> list[Sample]:
    samples: list[Sample] = []
    with path.open("r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            match = LINE_PATTERN.search(line)
            if not match:
                continue
            samples.append(
                Sample(
                    stage=match.group("stage"),
                    pts_us=int(match.group("pts")),
                    wall_ms=int(match.group("wall")),
                )
            )
    return samples


def first_per_pts(samples: list[Sample], stage: str) -> dict[int, int]:
    out: dict[int, int] = {}
    for sample in samples:
        if sample.stage != stage:
            continue
        if sample.pts_us not in out:
            out[sample.pts_us] = sample.wall_ms
    return out


def percentile(values: list[float], pct: float) -> float:
    if not values:
        return float("nan")
    values_sorted = sorted(values)
    rank = (len(values_sorted) - 1) * pct
    lower = int(rank)
    upper = min(lower + 1, len(values_sorted) - 1)
    fraction = rank - lower
    return values_sorted[lower] * (1 - fraction) + values_sorted[upper] * fraction


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print(f"usage: {argv[0]} <server.log> <viewer.log>", file=sys.stderr)
        return 2

    server_path = Path(argv[1])
    viewer_path = Path(argv[2])
    server_samples = parse(server_path)
    viewer_samples = parse(viewer_path)

    by_stage: dict[str, dict[int, int]] = {}
    for stage in SERVER_STAGES:
        by_stage[stage] = first_per_pts(server_samples, stage)
    for stage in VIEWER_STAGES:
        by_stage[stage] = first_per_pts(viewer_samples, stage)

    print("FRAMECOUNT analysis")
    print("===================")
    print(f"server:  {server_path}")
    print(f"viewer:  {viewer_path}")
    print()
    print("Per-stage frame counts (unique ptsUs):")
    for stage in ALL_STAGES:
        print(f"  {stage:8s}  {len(by_stage[stage]):6d}")
    print()

    enc_to_reasm: list[float] = []
    for pts, enc_wall in by_stage["enc"].items():
        reasm_wall = by_stage["reasm"].get(pts)
        if reasm_wall is not None:
            enc_to_reasm.append(reasm_wall - enc_wall)
    skew_offset = min(enc_to_reasm) if enc_to_reasm else 0.0
    print(
        f"Estimated server->viewer clock skew: {skew_offset:.1f} ms "
        f"(floor of enc->reasm; cross-source deltas below corrected by -{skew_offset:.1f})"
    )
    print()

    print("Per-stage latency deltas (ms):")
    print(f"  {'delta':46s}  {'n':>6}  {'p50':>7}  {'p95':>7}  {'min':>7}  {'max':>7}")
    for upstream, downstream, label, cross_source in DELTAS:
        upstream_map = by_stage[upstream]
        downstream_map = by_stage[downstream]
        deltas: list[float] = []
        for pts, upstream_wall in upstream_map.items():
            downstream_wall = downstream_map.get(pts)
            if downstream_wall is None:
                continue
            delta = downstream_wall - upstream_wall
            if cross_source:
                delta -= skew_offset
            deltas.append(delta)
        if not deltas:
            print(f"  {label:46s}  {0:>6}  {'-':>7}  {'-':>7}  {'-':>7}  {'-':>7}")
            continue
        p50 = percentile(deltas, 0.50)
        p95 = percentile(deltas, 0.95)
        print(
            f"  {label:46s}  {len(deltas):>6d}  "
            f"{p50:>7.1f}  {p95:>7.1f}  "
            f"{min(deltas):>7.1f}  {max(deltas):>7.1f}"
        )

    print()
    convert_map = by_stage["convert"]
    enc_map = by_stage["enc"]
    queue_depths: list[int] = []
    convert_pts_sorted = sorted(convert_map.keys())
    for index, pts in enumerate(convert_pts_sorted):
        if pts not in enc_map:
            continue
        enc_wall = enc_map[pts]
        depth = (
            sum(
                1
                for earlier in convert_pts_sorted[:index]
                if earlier in enc_map
                and enc_map[earlier] > convert_map[pts]
                and enc_map[earlier] <= enc_wall
            )
            + 1
        )
        queue_depths.append(depth)
    if queue_depths:
        print(
            f"NVENC queue depth (convert->enc, frames): "
            f"median={statistics.median(queue_depths):.1f}, "
            f"p95={percentile([float(x) for x in queue_depths], 0.95):.1f}, "
            f"max={max(queue_depths)}"
        )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
