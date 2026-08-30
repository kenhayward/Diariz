"""Measure the per-chunk floor through the REST API, with no shell access to the box.

Uploads each clip as a recording, waits for it to transcribe, and reads back
Transcription.ProcessingMs - the worker's own full-pipeline wall clock (blob download + ASR +
alignment + diarization + voiceprint embedding). That is exactly the number that decides
whether live chunked transcription keeps up.

Less informative than chunk_floor.py (no per-stage breakdown) but needs only a token.

    DIARIZ_TOKEN=dz_api_... python api_floor.py --base http://host:8080 --clips ./clips

SIDE EFFECTS on the target instance - read before running:
  - Creates one recording per clip per repeat. Deleted afterwards unless --keep is passed.
  - Each transcription may trigger the owner's summary / actions / tags / embedding jobs,
    which spend real LLM calls. Use --only to shorten the ladder if that matters, or prefer
    chunk_floor.py, which touches nothing.
  - Runs on the shared GPU queue, so a clip can sit behind a real job. Queue wait is reported
    separately from ProcessingMs so contention stays visible instead of inflating results.
"""
import argparse
import glob
import json
import mimetypes
import os
import statistics
import sys
import time
import uuid

import _client

POLL_S = 2.0
# Transcribed is enough - the later summarisation states do not change ProcessingMs.
DONE = {"Transcribed", "Summarized", "Summarizing"}
FAILED = {"Failed"}


def multipart(fields: dict, file_field: str, path: str) -> tuple[bytes, str]:
    """Build a multipart/form-data body. Stdlib only - no requests dependency."""
    boundary = f"----diariz{uuid.uuid4().hex}"
    out = bytearray()
    for k, v in fields.items():
        out += f"--{boundary}\r\n".encode()
        out += f'Content-Disposition: form-data; name="{k}"\r\n\r\n'.encode()
        out += f"{v}\r\n".encode()
    name = os.path.basename(path)
    ctype = mimetypes.guess_type(name)[0] or "application/octet-stream"
    with open(path, "rb") as f:
        data = f.read()
    out += f"--{boundary}\r\n".encode()
    out += f'Content-Disposition: form-data; name="{file_field}"; filename="{name}"\r\n'.encode()
    out += f"Content-Type: {ctype}\r\n\r\n".encode()
    out += data + b"\r\n"
    out += f"--{boundary}--\r\n".encode()
    return bytes(out), f"multipart/form-data; boundary={boundary}"


def measure(base: str, token: str, path: str, secs: float, timeout_s: float) -> dict:
    body, ctype = multipart(
        {"title": f"{_client.MARKER} {secs:.0f}s", "durationMs": int(secs * 1000), "source": "Upload"},
        "audio", path)

    t_upload = time.perf_counter()
    status, resp = _client.request("POST", f"{base}/api/recordings", token, body, ctype, timeout=300)
    upload_s = time.perf_counter() - t_upload
    if status not in (200, 201):
        return {"error": f"upload HTTP {status}: {str(resp)[:200]}"}

    rec_id = resp["id"]
    t0 = time.perf_counter()
    last = None
    while time.perf_counter() - t0 < timeout_s:
        time.sleep(POLL_S)
        status, detail = _client.request("GET", f"{base}/api/recordings/{rec_id}", token)
        if status != 200:
            return {"id": rec_id, "error": f"poll HTTP {status}"}
        last = detail.get("status")
        if last in FAILED:
            return {"id": rec_id, "error": f"transcription failed: {detail.get('error')}"}
        # RecordingDetailDto names the current transcription "Current", not "transcription".
        if last in DONE and detail.get("current"):
            wall_s = time.perf_counter() - t0
            tr = detail["current"]
            proc_ms = tr.get("processingMs")
            return {
                "id": rec_id,
                "upload_s": upload_s,
                "wall_s": wall_s,
                "processing_s": (proc_ms / 1000.0) if proc_ms else None,
                "queue_s": (wall_s - proc_ms / 1000.0) if proc_ms else None,
                "segments": len(tr.get("segments") or []),
                "speakers": len(detail.get("speakerNames") or {}),
            }
    return {"id": rec_id, "error": f"timed out after {timeout_s:.0f}s (last status {last})"}


def main() -> None:
    ap = argparse.ArgumentParser()
    _client.add_common_args(ap)
    ap.add_argument("--clips", required=True, help="directory of clip-NNNs.wav files")
    ap.add_argument("--repeats", type=int, default=3)
    ap.add_argument("--only", default=None,
                    help="comma-separated clip lengths to run, e.g. 20,30,45 (default: all)")
    ap.add_argument("--keep", action="store_true", help="do not delete the test recordings afterwards")
    ap.add_argument("--timeout", type=float, default=300)
    ap.add_argument("--json", dest="json_out", default=None)
    args = ap.parse_args()

    base, token = _client.resolve(args)
    health = _client.get_json(base, "/health", token, timeout=15)
    print(f"target        {base}   version {health.get('version')}")

    ladder = []
    for p in sorted(glob.glob(os.path.join(args.clips, "*.wav"))):
        try:
            ladder.append((float(os.path.splitext(os.path.basename(p))[0].split("-")[-1].rstrip("s")), p))
        except ValueError:
            continue
    if args.only:
        wanted = {float(x) for x in args.only.split(",")}
        ladder = [(s, p) for s, p in ladder if s in wanted]
    if not ladder:
        sys.exit("no clips selected")

    print(f"clips         {len(ladder)}   repeats {args.repeats}   = {len(ladder) * args.repeats} uploads")
    print(f"cleanup       {'NO - recordings kept' if args.keep else 'yes - recordings deleted after'}")
    print("note          each upload may also spend the owner's summary/actions/tags LLM calls\n")

    header = f"{'clip':>7} {'proc':>8} {'xRT':>7} {'queue':>8} {'wall':>8} {'segs':>5}"
    print(header)
    print("-" * len(header))

    results, created = [], []
    for secs, path in ladder:
        runs = []
        for _ in range(args.repeats):
            r = measure(base, token, path, secs, args.timeout)
            if r.get("id"):
                created.append(r["id"])
            if r.get("error"):
                print(f"{secs:>6.0f}s   ERROR  {r['error']}")
                continue
            runs.append(r)
        procs = [r["processing_s"] for r in runs if r["processing_s"] is not None]
        if not procs:
            continue
        med = statistics.median(procs)
        xrt = med / secs
        q = statistics.median([r["queue_s"] for r in runs if r["queue_s"] is not None] or [0])
        w = statistics.median([r["wall_s"] for r in runs])
        flag = "  <-- SLOWER THAN REALTIME" if xrt >= 1.0 else ""
        print(f"{secs:>6.0f}s {med:>7.2f}s {xrt:>6.2f}x {q:>7.1f}s {w:>7.1f}s "
              f"{runs[0]['segments']:>5}{flag}")
        results.append({"clip_seconds": secs, "processing_median_s": med,
                        "processing_min_s": min(procs), "processing_max_s": max(procs),
                        "realtime_factor": xrt, "queue_median_s": q, "wall_median_s": w,
                        "segments": runs[0]["segments"], "speakers": runs[0]["speakers"]})

    if not args.keep and created:
        print(f"\ncleaning up {len(created)} test recordings...")
        removed = sum(1 for rid in created
                      if _client.request("DELETE", f"{base}/api/recordings/{rid}", token, timeout=60)[0]
                      in (200, 204))
        print(f"deleted {removed}/{len(created)}")
        if removed != len(created):
            print("run cleanup.py to remove the rest")

    if len(results) >= 2:
        # Shares chunk_floor's caveat: a negative floor means the ladder has a step change.
        xs = [r["clip_seconds"] for r in results]
        ys = [r["processing_median_s"] for r in results]
        n = len(xs)
        mx, my = sum(xs) / n, sum(ys) / n
        denom = sum((x - mx) ** 2 for x in xs)
        slope = sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / denom if denom else 0.0
        floor = my - slope * mx
        print("\n" + "=" * 62)
        print(f"fixed floor per job   {floor:6.2f} s  (paid regardless of chunk length)")
        if slope > 0:
            print(f"marginal cost         {slope:6.3f} s per second of audio ({1 / slope:.2f}x realtime)")
        print()
        for target in (20, 30, 45, 60):
            pred = floor + slope * target
            verdict = "KEEPS UP" if pred < target * 0.8 else ("TIGHT" if pred < target else "FALLS BEHIND")
            print(f"  {target:>2}s chunk -> {pred:5.1f}s   {(target - pred) / target * 100:+6.1f}% headroom   {verdict}")
        print("\nPrefer a measured row over the fit where one exists - the fit is least accurate")
        print("in the middle of the ladder, which is exactly where the live chunk sizes are.")

    if args.json_out:
        with open(args.json_out, "w") as f:
            json.dump(results, f, indent=2)
        print(f"\nraw results -> {args.json_out}")


if __name__ == "__main__":
    main()
