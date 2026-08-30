"""Check the synthetic-clip fit against real recordings already on an instance.

Synthetic TTS is cleaner than a real meeting - no overlap, no room noise, few speakers - so it
could understate diarization cost. This compares the fit against audio that has actually been
through the pipeline.

Reports ONLY summary calculations: counts, ratios, percentiles. No names, no titles, no
transcript text, nothing that identifies a recording or a person. Keep it that way - the repo
is public and this is the one tool here that reads production data.

    DIARIZ_TOKEN=dz_api_... python validate.py --base http://host:8080 --floor 1.26 --slope 0.029
"""
import argparse
import statistics

import _client


def main() -> None:
    ap = argparse.ArgumentParser()
    _client.add_common_args(ap)
    ap.add_argument("--sample", type=int, default=60, help="how many recordings to inspect")
    ap.add_argument("--min-seconds", type=float, default=30, help="ignore anything shorter")
    ap.add_argument("--floor", type=float, required=True, help="fitted fixed cost, seconds")
    ap.add_argument("--slope", type=float, required=True, help="fitted seconds per second of audio")
    args = ap.parse_args()

    base, token = _client.resolve(args)

    items = [x for x in _client.list_recordings(base, token)
             if (x.get("durationMs") or 0) > args.min_seconds * 1000]
    # Longest first: those exercise the pipeline hardest and are the useful check.
    items.sort(key=lambda x: -(x.get("durationMs") or 0))
    items = items[:args.sample]
    print(f"sampling {len(items)} real recordings over {args.min_seconds:.0f}s, longest first\n")

    rows = []
    for rec in items:
        status, d = _client.request("GET", f"{base}/api/recordings/{rec['id']}", token)
        if status != 200:
            continue
        cur = d.get("current") or {}
        proc_ms, dur_ms = cur.get("processingMs"), d.get("durationMs") or 0
        if not proc_ms or dur_ms <= 0:
            continue
        if (cur.get("model") or "") == "merged":
            continue  # a concatenated transcript never went through the worker
        rows.append({"dur_s": dur_ms / 1000.0, "proc_s": proc_ms / 1000.0,
                     "xrt": proc_ms / dur_ms, "speakers": len(d.get("speakerNames") or {})})

    if not rows:
        raise SystemExit("no recordings with processingMs found (only newer transcriptions carry it)")

    xrts = sorted(r["xrt"] for r in rows)
    durs = sorted(r["dur_s"] for r in rows)
    print(f"usable sample          {len(rows)} recordings")
    print(f"audio length           median {statistics.median(durs) / 60:.1f} min, "
          f"max {max(durs) / 60:.1f} min, total {sum(durs) / 3600:.1f} h")
    print(f"speakers per recording median {statistics.median([r['speakers'] for r in rows]):.0f}, "
          f"max {max(r['speakers'] for r in rows)}")
    print()
    print("realtime factor (processing time / audio length) - lower is faster")
    print(f"  p10 {xrts[len(xrts) // 10]:.4f}   median {statistics.median(xrts):.4f}   "
          f"p90 {xrts[len(xrts) * 9 // 10]:.4f}   worst {max(xrts):.4f}")
    print()

    predicted = [args.floor + args.slope * r["dur_s"] for r in rows]
    optimistic = sum(1 for r, p in zip(rows, predicted) if r["proc_s"] > p)
    ratio = statistics.median([r["proc_s"] / p for r, p in zip(rows, predicted)])
    print(f"synthetic fit ({args.floor}s + {args.slope}s/s) vs real audio:")
    print(f"  fit is optimistic for {optimistic}/{len(rows)} recordings")
    print(f"  real audio is ~{ratio:.2f}x the synthetic cost at the same length")
    print()
    for target in (20, 30, 45, 60):
        pred = (args.floor + args.slope * target) * ratio
        print(f"  {target:>2}s chunk -> {pred:5.2f}s adjusted   "
              f"{(target - pred) / target * 100:+5.1f}% headroom")


if __name__ == "__main__":
    main()
