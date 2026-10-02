# Diarization spike harness

Throwaway evaluation tooling for [PLAN.md](PLAN.md). Not part of any deployable, not in CI, and not part
of the release checklist.

**Data stays outside the repo.** Every command mounts a data directory (`DATA` below, e.g.
`C:\Users\kenha\diariz-spike-data`) at `/data`. It holds real meeting audio. Reports written there
(`out/report.md`, `out/scores.csv`) carry counts and rates only and are safe to paste. `eval/private_map.json`
maps `recNNN` / `PNNN` back to database ids and must never leave the machine.

From Git Bash, prefix `docker` commands with `MSYS_NO_PATHCONV=1` so container paths are not rewritten.

## 1. Restore the backup into a scratch Postgres

```bash
unzip -p deploy/diariz-backup-*.zip database.dump > "$DATA/database.dump"
docker network create diariz-spike
docker run -d --name diariz-spike-pg --network diariz-spike -e POSTGRES_PASSWORD=spike -e POSTGRES_DB=diariz \
  -v "$DATA:/data" pgvector/pgvector:pg16
docker exec diariz-spike-pg pg_restore -U postgres -d diariz --no-owner --no-privileges /data/database.dump
```

## 2. Build the evaluation set

```bash
docker build -t diariz-spike-harness spikes/diarization/harness
docker run --rm --network diariz-spike -v "$BACKUP_ZIP:/backup/backup.zip:ro" -v "$DATA:/data" \
  diariz-spike-harness python build_evalset.py --dry-run          # counts only
docker run --rm --network diariz-spike -v "$BACKUP_ZIP:/backup/backup.zip:ro" -v "$DATA:/data" \
  diariz-spike-harness python build_evalset.py --per-bucket 6 --max-minutes 30
```

## 3. Run a candidate

```bash
cd spikes/diarization/candidates
docker build -f pyannote/Dockerfile -t diariz-spike-pyannote .
docker run --rm --gpus all -e HF_TOKEN -e PIPELINE=pyannote/speaker-diarization-community-1 \
  -e NAME=pyannote-community-1 -v "$DATA:/data" diariz-spike-pyannote [recNNN ...]
```

A candidate writes `/data/out/<name>/<uri>.rttm` and one `runs.jsonl` line per file (audio seconds, wall
seconds, peak VRAM). Keep the GPU otherwise idle while one runs: VRAM is sampled at device level.

## 4. Score

```bash
docker run --rm -v "$DATA:/data" diariz-spike-harness python score.py
```

## Tests

```bash
docker run --rm diariz-spike-harness python -m pytest -q
```
