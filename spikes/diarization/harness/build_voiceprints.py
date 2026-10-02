"""Identification set: one clip per (person, recording) for every manually named speaker with audio.

  python build_voiceprints.py --dry-run
  python build_voiceprints.py            # -> /data/vp/clips/<P>/<recNNN>.wav, /data/vp/manifest.json

Only speakers a person named by hand: `IdentifiedAuto` speakers were labelled by ECAPA itself, so
counting them would score ECAPA against its own answers. Multi-speaker labels are excluded too.
A clip pools that speaker's segments up to --seconds (the worker's EMBED_MAX_SECONDS, 120), and is kept
only with at least --min-seconds of speech (production identifies on 3 s; 5 s here, to stay above it).
"""
from __future__ import annotations

import argparse
import json
import os
import zipfile
from collections import Counter, defaultdict
from pathlib import Path

import psycopg
import soundfile as sf

from build_evalset import concat_spans, stable_order
from rttm import Turn, merge_turns

SQL = """
with cur as (
  select distinct on (t."RecordingId") t."RecordingId", t."Id" tid
  from "Transcriptions" t order by t."RecordingId", t."Version" desc)
select r."Id", r."BlobKey", sp."ProfileId", g."StartMs", g."EndMs"
from "Recordings" r
join cur c on c."RecordingId" = r."Id"
join "Speakers" sp on sp."RecordingId" = r."Id" and sp."ProfileId" is not null
     and not sp."IdentifiedAuto" and not sp."IsMultiSpeaker"
join "SpeakerProfiles" p on p."Id" = sp."ProfileId" and not p."VoiceprintOptOut"
join "Segments" g on g."TranscriptionId" = c.tid and g."SpeakerLabel" = sp."Label"
where r."AudioDeletedAt" is null
"""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dsn", default=os.getenv("SPIKE_DSN", "postgresql://postgres:spike@diariz-spike-pg/diariz"))
    ap.add_argument("--backup", default="/backup/backup.zip")
    ap.add_argument("--seconds", type=float, default=120)
    ap.add_argument("--min-seconds", type=float, default=5)
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    spans = defaultdict(list)  # (rec, profile) -> turns
    blobs = {}
    with psycopg.connect(args.dsn) as conn:
        for rid, blob, pid, s, e in conn.execute(SQL):
            spans[(str(rid), str(pid))].append(Turn(s / 1000, e / 1000, "x"))
            blobs[str(rid)] = blob
    total = {k: sum(t.end - t.start for t in v) for k, v in spans.items()}
    keep = {k: v for k, v in spans.items() if total[k] >= args.min_seconds}
    per_person = Counter(p for _, p in keep)
    print(f"clips: {len(keep)} from {len({r for r, _ in keep})} recordings, {len(per_person)} people; "
          f"{sum(1 for n in per_person.values() if n >= 2)} people in 2+ recordings "
          f"({sum(n for n in per_person.values() if n >= 2)} clips usable as probes)")
    if args.dry_run:
        return

    out = Path("/data/vp")
    (out / "clips").mkdir(parents=True, exist_ok=True)
    (out / "_raw").mkdir(exist_ok=True)
    rec_ids = {r: f"rec{i:03d}" for i, r in enumerate(sorted({r for r, _ in keep}, key=stable_order), 1)}
    person_ids = {p: f"P{i:03d}" for i, p in enumerate(sorted(per_person, key=stable_order), 1)}
    zf = zipfile.ZipFile(args.backup)
    manifest = []
    by_rec = defaultdict(list)
    for (r, p), turns in keep.items():
        by_rec[r].append((p, turns))
    for r, people in sorted(by_rec.items(), key=lambda kv: rec_ids[kv[0]]):
        raw = out / "_raw" / f"x{Path(blobs[r]).suffix}"
        with zf.open(f"objects/{blobs[r]}") as s, open(raw, "wb") as d:
            while chunk := s.read(1 << 20):
                d.write(chunk)
        for p, turns in people:
            chosen, used = [], 0.0
            for t in merge_turns(turns, 0.3):
                take = min(t.end - t.start, args.seconds - used)
                if take <= 0.5:
                    break
                chosen.append((t.start, t.start + take))
                used += take
            (out / "clips" / person_ids[p]).mkdir(exist_ok=True)
            clip = out / "clips" / person_ids[p] / f"{rec_ids[r]}.wav"
            concat_spans(raw, clip, chosen)
            # Segment times can run past the end of the stored audio, so the span total above can promise
            # more than the clip holds. Judge the clip by what was actually extracted.
            if sf.info(clip).duration < args.min_seconds:
                clip.unlink()
                continue
            manifest.append({"person": person_ids[p], "rec": rec_ids[r], "seconds": round(used, 1)})
        raw.unlink()
        print(f"{rec_ids[r]}: {len(people)} clips", flush=True)
    (out / "_raw").rmdir()
    (out / "manifest.json").write_text(json.dumps(manifest, indent=1))
    (out / "private_map.json").write_text(json.dumps(
        {"recordings": {v: k for k, v in rec_ids.items()}, "people": {v: k for k, v in person_ids.items()}}))


if __name__ == "__main__":
    main()
