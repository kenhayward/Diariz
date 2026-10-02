"""Gold set: short excerpts from the *hard* recordings, for hand labelling.

The silver set only admits recordings where 90% of speech was named, which selects the ones that
diarized cleanly. Over-splitting lives in the rest, so the gold excerpts come from there: recordings
with the most diarized labels per named person (or with a label flagged multi-speaker).

  python build_gold.py --count 10 --minutes 5     # -> /data/gold/{audio,uem,manifest.json,private_map.json}
  python build_gold.py --labels                   # after candidates ran on SET=gold: pre-filled label files

Labelling: open audio/<uri>.wav in Audacity, File > Import > Labels... labels/<uri>.txt, correct every
turn (boundaries and speaker), then File > Export > Export Labels... to labels/<uri>.done.txt. Use any
consistent speaker names; they are replaced by S01, S02... when the reference is built, and the
labels directory stays in the data folder. Then `python build_gold.py --refs`.

Pre-fill alternates between two candidates' output (recorded in the manifest), so a labeller who
leans on the pre-fill does not favour one candidate across the whole set.
"""
from __future__ import annotations

import argparse
import json
import os
import zipfile
from pathlib import Path

import psycopg

from build_evalset import SOURCES, stable_order, to_wav
from rttm import Turn, merge_turns, read_rttm, write_rttm

HARD_SQL = """
with cur as (
  select distinct on (t."RecordingId") t."RecordingId", t."Id" tid, t."Model"
  from "Transcriptions" t order by t."RecordingId", t."Version" desc),
lab as (
  select c."RecordingId", count(distinct g."SpeakerLabel") labels from cur c
  join "Segments" g on g."TranscriptionId" = c.tid group by 1),
ppl as (
  select "RecordingId", count(distinct "ProfileId") people, bool_or("IsMultiSpeaker") multi
  from "Speakers" group by 1)
select r."Id", r."BlobKey", r."Source", r."DurationMs", l.labels, coalesce(p.people, 0), coalesce(p.multi, false)
from "Recordings" r join cur c on c."RecordingId" = r."Id" join lab l on l."RecordingId" = r."Id"
left join ppl p on p."RecordingId" = r."Id"
where r."AudioDeletedAt" is null and c."Model" = 'whisperx-large-v3' and r."DurationMs" >= 15 * 60000
"""
PREFILL = ["pyannote-3.1-regular", "nemotron_offline_default"]


def build(args):
    silver = set(json.loads(Path("/data/eval/private_map.json").read_text())["recordings"].values())
    with psycopg.connect(args.dsn) as conn:
        rows = [r for r in conn.execute(HARD_SQL) if str(r[0]) not in silver]
    # Hardest first: most labels per named person; multi-speaker flags count as hard too.
    rows.sort(key=lambda r: (-(r[4] / max(r[5], 1) + (2 if r[6] else 0)), stable_order(str(r[0]))))
    picked = rows[: args.count]
    out = Path("/data/gold")
    for sub in ("audio", "uem", "labels", "ref", "_raw"):
        (out / sub).mkdir(parents=True, exist_ok=True)
    zf = zipfile.ZipFile(args.backup)
    manifest, private = {}, {"recordings": {}}
    for i, (rid, blob, source, dur, labels, people, multi) in enumerate(picked, 1):
        uri = f"gold{i:02d}"
        raw = out / "_raw" / f"{uri}{Path(blob).suffix}"
        with zf.open(f"objects/{blob}") as s, open(raw, "wb") as d:
            while chunk := s.read(1 << 20):
                d.write(chunk)
        start = 5 * 60  # skip the opening minutes, which are often set-up chatter
        length = args.minutes * 60
        to_wav(raw, out / "audio" / f"{uri}.wav", start, length)
        raw.unlink()
        (out / "uem" / f"{uri}.uem").write_text(f"{uri} 1 0.000 {length:.3f}\n")
        manifest[uri] = {"bucket": "gold", "source": SOURCES[source], "minutes": args.minutes,
                         "prod_labels": labels, "named_people": people, "multi_flag": multi,
                         "prefill": PREFILL[(i - 1) % len(PREFILL)]}
        private["recordings"][uri] = {"id": str(rid), "excerpt_start_s": start}
        print(uri, manifest[uri], flush=True)
    (out / "_raw").rmdir()
    (out / "manifest.json").write_text(json.dumps(manifest, indent=2))
    (out / "private_map.json").write_text(json.dumps(private, indent=2))


def labels():
    out = Path("/data/gold")
    manifest = json.loads((out / "manifest.json").read_text())
    for uri, meta in manifest.items():
        hyp = Path("/data/out/gold") / meta["prefill"] / f"{uri}.rttm"
        turns = merge_turns(read_rttm(hyp.read_text()).get(uri, []), max_gap=1.0)
        (out / "labels" / f"{uri}.txt").write_text(
            "".join(f"{t.start:.3f}\t{t.end:.3f}\t{t.speaker}\n" for t in turns))
        print(f"{uri}: {len(turns)} pre-filled turns from {meta['prefill']}")


def refs():
    out = Path("/data/gold")
    for done in sorted((out / "labels").glob("*.done.txt")):
        uri = done.name.split(".")[0]
        ids: dict[str, str] = {}
        turns = []
        for line in done.read_text().splitlines():
            p = line.split("\t")
            if len(p) < 3 or not p[2].strip():
                continue
            spk = ids.setdefault(p[2].strip(), f"S{len(ids) + 1:02d}")
            turns.append(Turn(float(p[0]), float(p[1]), spk))
        (out / "ref" / f"{uri}.rttm").write_text(write_rttm(uri, sorted(turns, key=lambda t: t.start)))
        print(f"{uri}: {len(turns)} turns, {len(ids)} speakers")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--dsn", default=os.getenv("SPIKE_DSN", "postgresql://postgres:spike@diariz-spike-pg/diariz"))
    ap.add_argument("--backup", default="/backup/backup.zip")
    ap.add_argument("--count", type=int, default=10)
    ap.add_argument("--minutes", type=float, default=5)
    ap.add_argument("--labels", action="store_true")
    ap.add_argument("--refs", action="store_true")
    a = ap.parse_args()
    labels() if a.labels else refs() if a.refs else build(a)
