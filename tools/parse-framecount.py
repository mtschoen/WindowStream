#!/usr/bin/env python3
"""Parse WindowStream FRAMECOUNT logs (server stderr + viewer logcat) and
compute per-stage pipeline latency percentiles, joined on ptsUs.

Usage: parse-framecount.py <viewer_framecount.log> <server_stderr.log>
"""

import re
import sys

PAT = re.compile(r"stage=(\w+)\s+ptsUs=(\d+)\s+wallMs=(\d+)")


def parse(path, keep):
    d = {}
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = PAT.search(line)
            if not m:
                continue
            stage, pts, wall = m.group(1), int(m.group(2)), int(m.group(3))
            if stage in keep:
                d.setdefault(pts, {})[stage] = wall
    return d


def pct(vals, p):
    if not vals:
        return None
    vals = sorted(vals)
    k = (len(vals) - 1) * p / 100.0
    f = int(k)
    c = min(f + 1, len(vals) - 1)
    return vals[f] + (vals[c] - vals[f]) * (k - f)


def within(d, a, b):
    return [st[b] - st[a] for st in d.values() if a in st and b in st]


def cross(server, viewer, s_stage, v_stage, skew):
    out = []
    for pts, sv in server.items():
        vv = viewer.get(pts)
        if vv and s_stage in sv and v_stage in vv:
            out.append((vv[v_stage] - sv[s_stage]) - skew)
    return out


def main(argv: list[str]) -> int:
    viewer = parse(argv[1], {"reasm", "dec", "present"})
    server = parse(argv[2], {"convert", "enc"})

    # Estimate server->viewer clock skew so the fastest network transit (enc->reasm)
    # floors at ~0ms, matching the 2026-05-11 baseline methodology (network floor=0).
    raw_net = cross(server, viewer, "enc", "reasm", 0)
    skew = min(raw_net) if raw_net else 0

    rows = [
        ("convert -> enc   (server, NVENC)", within(server, "convert", "enc")),
        ("enc -> reasm     (network*)", cross(server, viewer, "enc", "reasm", skew)),
        ("reasm -> dec     (decode) <<", within(viewer, "reasm", "dec")),
        ("dec -> present   (vsync)", within(viewer, "dec", "present")),
        ("reasm -> present (viewer total)", within(viewer, "reasm", "present")),
        ("convert->present (E2E*)", cross(server, viewer, "convert", "present", skew)),
    ]

    print(
        f"skew estimate (server->viewer) = {skew} ms  (* = skew-corrected cross-device)"
    )
    print(f"{'stage':<34}{'p50':>6}{'p95':>6}{'min':>6}{'max':>6}{'n':>7}")
    print("-" * 65)
    for name, vals in rows:
        if not vals:
            print(f"{name:<34}{'--':>6}")
            continue
        print(
            f"{name:<34}{pct(vals, 50):>6.0f}{pct(vals, 95):>6.0f}{min(vals):>6.0f}{max(vals):>6.0f}{len(vals):>7}"
        )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
