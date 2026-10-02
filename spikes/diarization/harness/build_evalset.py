"""Build the diarization evaluation set from a platform backup (restored into a scratch Postgres).

Everything is written under OUT (default /data/eval), which must live OUTSIDE the repository: it holds
real meeting audio. Nothing here prints a name, title or transcript text - only counts.

Outputs:
  audio/<uri>.wav          16 kHz mono excerpt (first --max-minutes of the recording)
  ref/<uri>.rttm           silver reference: the current transcript's speaker turns, with labels the user
                           named as the same person merged (see rttm.reference_from_segments)
  uem/<uri>.uem            the scored region (whole excerpt)
  voiceprints/<P>/<uri>.wav  up to 120 s of one named person's speech per recording (identification track)
  manifest.json            uri -> bucket, source, minutes, reference speaker count (no names)
  private_map.json         uri -> recording id, P -> profile id. Local only, for tracing back.

Usage (inside the harness image, on the spike network):
  python build_evalset.py --dry-run
  python build_evalset.py --per-bucket 6 --max-minutes 30
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import zipfile
from collections import Counter, defaultdict
from pathlib import Path

import psycopg

from rttm import Turn, clip_turns, merge_turns, reference_from_segments, write_rttm

SOURCES = {0: "mic", 1: "system", 2: "upload", 3: "combined"}

# Current (highest-version) transcription per recording that still has audio, with each diarized label's
# speech time and the person it was named as. IsMultiSpeaker labels make the reference wrong by
# definition, so those recordings are excluded below.
LABELS_SQL = """
with cur as (
  select distinct on (t."RecordingId") t."RecordingId", t."Id" as tid, t."Model"
  from "Transcriptions" t order by t."RecordingId", t."Version" desc)
select r."Id", r."BlobKey", r."Source", r."DurationMs", c."Model", g."SpeakerLabel",
       sp."ProfileId", coalesce(sp."IsMultiSpeaker", false), sum(g."EndMs" - g."StartMs")
from "Recordings" r
join cur c on c."RecordingId" = r."Id"
join "Segments" g on g."TranscriptionId" = c.tid
left join "Speakers" sp on sp."RecordingId" = r."Id" and sp."Label" = g."SpeakerLabel"
where r."AudioDeletedAt" is null
group by 1,2,3,4,5,6,7,8
"""

SEGMENTS_SQL = """
with cur as (
  select distinct on (t."RecordingId") t."Id" as tid from "Transcriptions" t
  where t."RecordingId" = %s order by t."RecordingId", t."Version" desc)
select g."StartMs", g."EndMs", g."SpeakerLabel" from "Segments" g join cur on g."TranscriptionId" = cur.tid
"""


def bucket(people: int) -> str:
    return "2" if people <= 2 else "3-4" if people <= 4 else "5-8" if people <= 8 else "9+"


def stable_order(rec_id: str) -> str:
    # Deterministic but not alphabetical, so re-running picks the same recordings.
    return hashlib.sha256(rec_id.encode()).hexdigest()


def load_candidates(conn, min_named: float, min_minutes: float):
    recs: dict = {}
    for rid, blob, source, dur, model, label, profile, multi, ms in conn.execute(LABELS_SQL):
        r = recs.setdefault(str(rid), {"blob": blob, "source": source, "dur": dur or 0, "model": model,
                                      "labels": {}, "multi": False})
        r["labels"][label] = (str(profile) if profile else None, int(ms or 0))  # sum() is a Decimal
        r["multi"] |= multi
    out, rejected = [], Counter()
    for rid, r in recs.items():
        total = sum(ms for _, ms in r["labels"].values()) or 1
        named = sum(ms for p, ms in r["labels"].values() if p) / total
        people = len({p for p, _ in r["labels"].values() if p})
        if r["model"] != "whisperx-large-v3":
            rejected["not a full-file transcription"] += 1
        elif r["multi"]:
            rejected["has a multi-speaker label"] += 1
        elif r["dur"] < min_minutes * 60_000:
            rejected["too short"] += 1
        elif named < min_named:
            rejected[f"under {min_named:.0%} of speech named"] += 1
        elif people < 2:
            rejected["fewer than 2 named people"] += 1
        else:
            out.append({"id": rid, **r, "named": named, "people": people, "bucket": bucket(people)})
    return out, rejected


def select(cands, per_bucket: int):
    by = defaultdict(list)
    for c in sorted(cands, key=lambda c: stable_order(c["id"])):
        by[c["bucket"]].append(c)
    picked = []
    for b, items in sorted(by.items()):
        # Round-robin over sources inside a bucket, so one capture path does not dominate it.
        by_src = defaultdict(list)
        for c in items:
            by_src[c["source"]].append(c)
        queues = list(by_src.values())
        while queues and sum(1 for c in picked if c["bucket"] == b) < per_bucket:
            q = queues.pop(0)
            picked.append(q.pop(0))
            if q:
                queues.append(q)
    return picked


def to_wav(src: Path, dst: Path, start: float = 0, duration: float | None = None):
    cmd = ["ffmpeg", "-nostdin", "-loglevel", "error", "-y", "-ss", str(start), "-i", str(src)]
    if duration:
        cmd += ["-t", str(duration)]
    subprocess.run(cmd + ["-ac", "1", "-ar", "16000", str(dst)], check=True)


def concat_spans(src: Path, dst: Path, spans: list[tuple[float, float]]):
    """Write the given (start, end) second spans of src, back to back, as one 16 kHz mono wav."""
    expr = "+".join(f"between(t,{a:.3f},{b:.3f})" for a, b in spans)
    subprocess.run(["ffmpeg", "-nostdin", "-loglevel", "error", "-y", "-i", str(src),
                    "-af", f"aselect='{expr}',asetpts=N/SR/TB", "-ac", "1", "-ar", "16000", str(dst)],
                   check=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dsn", default=os.getenv("SPIKE_DSN", "postgresql://postgres:spike@diariz-spike-pg/diariz"))
    ap.add_argument("--backup", default="/backup/backup.zip")
    ap.add_argument("--out", default="/data/eval")
    ap.add_argument("--per-bucket", type=int, default=6)
    ap.add_argument("--max-minutes", type=float, default=30)
    ap.add_argument("--min-minutes", type=float, default=5)
    ap.add_argument("--min-named", type=float, default=0.9)
    ap.add_argument("--voiceprint-seconds", type=float, default=120)
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    with psycopg.connect(args.dsn) as conn:
        cands, rejected = load_candidates(conn, args.min_named, args.min_minutes)
        picked = select(cands, args.per_bucket)
        print("eligible by bucket:", dict(sorted(Counter(c["bucket"] for c in cands).items())))
        print("rejected:", dict(rejected))
        print("picked by bucket:", dict(sorted(Counter(c["bucket"] for c in picked).items())))
        print("picked by source:", dict(Counter(SOURCES[c["source"]] for c in picked)))
        print("picked minutes (capped):", round(sum(min(c["dur"] / 60000, args.max_minutes) for c in picked)))
        if args.dry_run:
            return

        out = Path(args.out)
        for sub in ("audio", "ref", "uem", "voiceprints", "_raw"):
            (out / sub).mkdir(parents=True, exist_ok=True)
        zf = zipfile.ZipFile(args.backup)
        manifest, private = {}, {"recordings": {}, "profiles": {}}
        profile_ids: dict[str, str] = {}
        vp_counts = Counter()

        for i, c in enumerate(picked, 1):
            uri = f"rec{i:03d}"
            raw = out / "_raw" / f"{uri}{Path(c['blob']).suffix}"
            with zf.open(f"objects/{c['blob']}") as s, open(raw, "wb") as d:
                while chunk := s.read(1 << 20):
                    d.write(chunk)
            limit = args.max_minutes * 60
            to_wav(raw, out / "audio" / f"{uri}.wav", 0, limit)

            segs = list(conn.execute(SEGMENTS_SQL, (c["id"],)))
            people = {lbl: p for lbl, (p, _) in c["labels"].items() if p}
            ref = clip_turns(reference_from_segments(segs, people), 0, limit)
            (out / "ref" / f"{uri}.rttm").write_text(write_rttm(uri, ref))
            excerpt = min(c["dur"] / 1000, limit)
            (out / "uem" / f"{uri}.uem").write_text(f"{uri} 1 0.000 {excerpt:.3f}\n")

            # Identification track: each named person's speech in this recording (whole file, not the
            # excerpt), pooled to the same budget the worker uses (EMBED_MAX_SECONDS).
            by_person = defaultdict(list)
            for s_ms, e_ms, lbl in sorted(segs):
                if lbl in people:
                    by_person[people[lbl]].append(Turn(s_ms / 1000, e_ms / 1000, "x"))
            for pid, turns in by_person.items():
                anon = profile_ids.setdefault(pid, f"P{len(profile_ids) + 1:03d}")
                spans, total = [], 0.0
                for t in merge_turns(turns, 0.3):
                    take = min(t.end - t.start, args.voiceprint_seconds - total)
                    if take <= 0.5:
                        break
                    spans.append((t.start, t.start + take))
                    total += take
                if total >= 5:
                    (out / "voiceprints" / anon).mkdir(exist_ok=True)
                    concat_spans(raw, out / "voiceprints" / anon / f"{uri}.wav", spans)
                    vp_counts[anon] += 1
            raw.unlink()

            manifest[uri] = {"bucket": c["bucket"], "source": SOURCES[c["source"]],
                             "minutes": round(excerpt / 60, 1), "ref_speakers": len({t.speaker for t in ref}),
                             "named_share": round(c["named"], 3)}
            private["recordings"][uri] = c["id"]
            print(f"{uri}: {manifest[uri]}", flush=True)

        private["profiles"] = {v: k for k, v in profile_ids.items()}
        (out / "manifest.json").write_text(json.dumps(manifest, indent=2))
        (out / "private_map.json").write_text(json.dumps(private, indent=2))
        (out / "_raw").rmdir()
        multi = sum(1 for n in vp_counts.values() if n >= 2)
        print(f"voiceprint clips: {sum(vp_counts.values())} across {len(vp_counts)} people, "
              f"{multi} people in 2+ recordings (usable for identification)")


if __name__ == "__main__":
    main()
