"""Score every candidate's RTTM output against the reference and write a summary.

  python score.py                     # all candidates under /data/out
  python score.py --collar 0.25

Reads   /data/eval/{ref,uem}/<uri>.rttm|uem, /data/eval/manifest.json
        /data/out/<candidate>/<uri>.rttm, /data/out/<candidate>/runs.jsonl (timing + VRAM per uri)
Writes  /data/out/report.md and /data/out/scores.csv - counts and rates only, safe to paste.

On the silver reference, read **confusion** first. Its turns come from Whisper segments, which span the
short pauses inside a sentence, so missed speech and false alarm partly measure segment granularity
rather than diarization. Confusion (speech attributed to the wrong person) is what over-splitting and
merged speakers show up as, and it is the error a user actually sees.
"""
from __future__ import annotations

import argparse
import csv
import json
from collections import defaultdict
from pathlib import Path
from statistics import mean

from pyannote.core import Annotation, Segment, Timeline
from pyannote.metrics.diarization import DiarizationErrorRate, JaccardErrorRate

from rttm import read_rttm


def annotation(turns, uri):
    a = Annotation(uri=uri)
    for i, t in enumerate(turns):
        a[Segment(t.start, t.end), i] = t.speaker
    return a


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--eval", default="/data/eval")
    ap.add_argument("--out", default="/data/out")
    ap.add_argument("--collar", type=float, default=0.25)
    args = ap.parse_args()

    ev, out = Path(args.eval), Path(args.out)
    manifest = json.loads((ev / "manifest.json").read_text())
    rows = []
    for cand in sorted(p for p in out.iterdir() if p.is_dir()):
        runs = {}
        if (cand / "runs.jsonl").exists():
            for line in (cand / "runs.jsonl").read_text().splitlines():
                r = json.loads(line)
                runs[r["uri"]] = r
        for uri, meta in manifest.items():
            hyp_file = cand / f"{uri}.rttm"
            if not hyp_file.exists():
                continue
            ref = annotation(read_rttm((ev / "ref" / f"{uri}.rttm").read_text()).get(uri, []), uri)
            hyp = annotation(read_rttm(hyp_file.read_text()).get(uri, []), uri)
            _, _, s, e = (ev / "uem" / f"{uri}.uem").read_text().split()[:4]
            uem = Timeline([Segment(float(s), float(e))])
            der = DiarizationErrorRate(collar=args.collar)
            c = der(ref, hyp, uem=uem, detailed=True)
            total = c["total"] or 1
            jer = JaccardErrorRate(collar=args.collar)(ref, hyp, uem=uem)
            run = runs.get(uri, {})
            rows.append({
                "candidate": cand.name, "uri": uri, "bucket": meta["bucket"], "source": meta["source"],
                "der": c["diarization error rate"], "confusion": c["confusion"] / total,
                "missed": c["missed detection"] / total, "false_alarm": c["false alarm"] / total, "jer": jer,
                "ref_spk": len(ref.labels()), "hyp_spk": len(hyp.labels()),
                "rtf": run.get("wall_s", 0) / max(run.get("audio_s", 1), 1e-9) if run else None,
                "peak_vram_mb": run.get("peak_vram_mb"),
            })

    if not rows:
        print("nothing to score")
        return
    with open(out / "scores.csv", "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0]))
        w.writeheader()
        w.writerows(rows)

    def pct(x):
        return f"{100 * x:.1f}"

    lines = [f"# Diarization spike - scores (collar {args.collar}s)", "",
             "| candidate | files | DER % | confusion % | missed % | FA % | JER % | spk count err (mean abs) "
             "| over-split (hyp/ref spk) | RTF | peak VRAM MB |",
             "|---|---|---|---|---|---|---|---|---|---|---|"]
    by_cand = defaultdict(list)
    for r in rows:
        by_cand[r["candidate"]].append(r)
    for cand, rs in by_cand.items():
        rtfs = [r["rtf"] for r in rs if r["rtf"] is not None]
        vram = [r["peak_vram_mb"] for r in rs if r["peak_vram_mb"]]
        lines.append(
            f"| {cand} | {len(rs)} | {pct(mean(r['der'] for r in rs))} | {pct(mean(r['confusion'] for r in rs))} "
            f"| {pct(mean(r['missed'] for r in rs))} | {pct(mean(r['false_alarm'] for r in rs))} "
            f"| {pct(mean(r['jer'] for r in rs))} | {mean(abs(r['hyp_spk'] - r['ref_spk']) for r in rs):.1f} "
            f"| {mean(r['hyp_spk'] / max(r['ref_spk'], 1) for r in rs):.2f} "
            f"| {f'{mean(rtfs):.3f}' if rtfs else '-'} | {max(vram) if vram else '-'} |")
    lines += ["", "## Confusion % by speaker-count bucket", "",
              "| candidate | " + " | ".join(sorted({r['bucket'] for r in rows})) + " |",
              "|---|" + "---|" * len({r['bucket'] for r in rows})]
    for cand, rs in by_cand.items():
        cells = []
        for b in sorted({r['bucket'] for r in rows}):
            bs = [r for r in rs if r["bucket"] == b]
            cells.append(pct(mean(r["confusion"] for r in bs)) if bs else "-")
        lines.append(f"| {cand} | " + " | ".join(cells) + " |")
    (out / "report.md").write_text("\n".join(lines) + "\n")
    print("\n".join(lines))


if __name__ == "__main__":
    main()
