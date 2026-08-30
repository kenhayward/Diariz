"""Confirm diarization actually ran on the benchmark recordings.

Worth running whenever comparing two boxes. If the fast one were silently skipping
diarization - the most expensive stage, 75-80% of a chunk - every segment would land on one
label and the timing comparison would be meaningless. This shows the label distribution so
that failure is visible rather than assumed away.

    DIARIZ_TOKEN=dz_api_... python speakers.py --base http://host:8080

Only reports on recordings this benchmark created (title prefix "floor-bench"), so it never
prints anything about real recordings.
"""
import argparse
import collections

import _client


def main() -> None:
    ap = argparse.ArgumentParser()
    _client.add_common_args(ap)
    args = ap.parse_args()
    base, token = _client.resolve(args)

    mine = _client.bench_recordings(base, token)
    if not mine:
        raise SystemExit(f"no {_client.MARKER!r} recordings present right now")

    print(f"{'title':<20} {'segs':>5} {'labels':>7}  distribution")
    print("-" * 68)
    for rec in mine:
        status, d = _client.request("GET", f"{base}/api/recordings/{rec['id']}", token)
        if status != 200:
            print(f"{rec.get('title'):<20}  HTTP {status}")
            continue
        segs = (d.get("current") or {}).get("segments") or []
        counts = collections.Counter(s.get("speakerLabel") or s.get("speaker") or "?" for s in segs)
        warn = "   <-- single label, check diarization ran" if len(counts) < 2 and len(segs) > 3 else ""
        print(f"{rec.get('title'):<20} {len(segs):>5} {len(counts):>7}  {dict(counts)}{warn}")


if __name__ == "__main__":
    main()
