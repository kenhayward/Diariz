"""omi-sync: pull a recording session off an Omi card and into Diariz.

    python -m omi_sync /path/to/a01.txt --url https://diariz.example.com

See README.md for the full workflow. The short version: copy a01.txt off the card first,
run this, then delete it from the card so the next cycle starts clean.
"""

from __future__ import annotations

import argparse
import os
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Mapping, Optional

from .diariz import DiarizClient
from .framing import LAYOUTS
from .pipeline import iter_sessions, scan


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="omi-sync",
        description="Decode an Omi card file into sessions and upload them to Diariz.",
    )
    parser.add_argument("card", help="path to the card file (DevKit: a01.txt)")

    parser.add_argument("--device", choices=sorted(LAYOUTS), default="devkit",
                        help="which Omi wrote this file (default: devkit)")

    parser.add_argument("--url", default=os.environ.get("DIARIZ_URL"),
                        help="Diariz base URL, or set DIARIZ_URL")
    parser.add_argument("--token", default=None,
                        help="personal API token (dz_api_...), or set DIARIZ_TOKEN")
    parser.add_argument("--email", help="sign in with credentials instead of a token")
    parser.add_argument("--password", help="password for --email")

    parser.add_argument("--ended-at", default=None,
                        help="when you stopped the device / pulled the card, ISO 8601. "
                             "Everything is dated backwards from here (default: now)")

    parser.add_argument("--gap-minutes", type=float, default=5.0,
                        help="silence this long ends a session (default: 5)")
    parser.add_argument("--min-session-seconds", type=float, default=60.0,
                        help="discard sessions shorter than this (default: 60)")
    parser.add_argument("--max-session-hours", type=float, default=2.0,
                        help="split sessions longer than this (default: 2)")
    parser.add_argument("--threshold", type=int, default=None,
                        help="frame-length silence threshold in bytes; omit to choose "
                             "one automatically")
    parser.add_argument("--min-active-bytes", type=int, default=16,
                        help="frames at or below this size are always silence (default: 16)")

    parser.add_argument("--scan-only", action="store_true",
                        help="report what would be uploaded, then stop")
    parser.add_argument("--dry-run", action="store_true",
                        help="write .opus files locally instead of uploading")
    parser.add_argument("--out", default="omi-sessions",
                        help="directory for --dry-run output (default: omi-sessions)")

    return parser


def resolve_ended_at(value: Optional[str]) -> datetime:
    """When the recording stopped. Naive input is taken as local time, not UTC."""
    if value is None:
        return datetime.now(timezone.utc)
    try:
        parsed = datetime.fromisoformat(value.strip().replace("Z", "+00:00"))
    except ValueError:
        raise SystemExit(
            f"Could not read --ended-at {value!r}. Use ISO 8601, "
            "for example 2026-08-25T17:00:00 or 2026-08-25T17:00:00+01:00."
        )
    if parsed.tzinfo is None:
        parsed = parsed.astimezone()
    return parsed


def resolve_token(token: Optional[str], env: Mapping[str, str]) -> str:
    resolved = token or env.get("DIARIZ_TOKEN")
    if not resolved:
        raise SystemExit(
            "No Diariz credentials. Pass --token dz_api_... (Settings -> Developers), "
            "set DIARIZ_TOKEN, or use --email/--password. "
            "Use --dry-run to decode without uploading."
        )
    return resolved


def _human(ms: float) -> str:
    seconds = int(ms // 1000)
    hours, rest = divmod(seconds, 3600)
    minutes, secs = divmod(rest, 60)
    return f"{hours}h{minutes:02d}m{secs:02d}s" if hours else f"{minutes}m{secs:02d}s"


def run(args: argparse.Namespace, uploader: Any = None) -> int:
    card = Path(args.card)
    if not card.is_file():
        raise SystemExit(f"No such card file: {card}")

    layout = LAYOUTS[args.device]
    ended_at = resolve_ended_at(args.ended_at)

    limits = dict(
        threshold=args.threshold,
        gap_ms=int(args.gap_minutes * 60_000),
        min_session_ms=int(args.min_session_seconds * 1000),
        max_session_ms=int(args.max_session_hours * 3_600_000),
        min_active_bytes=args.min_active_bytes,
    )

    plan = scan(card, layout, **limits)
    total_ms = plan.total_frames * layout.frame_ms
    print(f"{card}: {plan.total_frames:,} frames, {_human(total_ms)} of audio "
          f"({layout.name} layout, {layout.frame_ms} ms frames)")
    print(f"silence threshold: "
          f"{plan.threshold if plan.threshold is not None else 'auto declined'} bytes "
          f"(floor {args.min_active_bytes})")
    print(f"found {len(plan.sessions)} session(s), "
          f"ending {ended_at.isoformat()}")

    if not plan.sessions:
        print("No sessions worth uploading. Try a smaller --min-session-seconds, "
              "a larger --gap-minutes, or an explicit --threshold.")
        return 0

    if args.scan_only:
        return 0

    if not args.dry_run and uploader is None:
        if not args.url:
            raise SystemExit("No Diariz URL. Pass --url or set DIARIZ_URL.")
        if args.email:
            token = DiarizClient.login(args.url, args.email, args.password or "")
        else:
            token = resolve_token(args.token, os.environ)
        uploader = DiarizClient(args.url, token)

    out_dir = Path(args.out)
    if args.dry_run:
        out_dir.mkdir(parents=True, exist_ok=True)

    failures = 0
    for session in iter_sessions(card, layout, ended_at=ended_at, plan=plan, **limits):
        blob = session.to_ogg_opus()
        label = (f"  {session.started_at:%Y-%m-%d %H:%M} "
                 f"{_human(session.duration_ms)} {len(blob) / 1e6:.1f} MB")

        if args.dry_run:
            (out_dir / session.filename).write_bytes(blob)
            print(f"{label} -> {out_dir / session.filename}")
            continue

        try:
            recording_id = uploader.upload(
                blob,
                filename=session.filename,
                title=session.title,
                started_at=session.started_at,
                duration_ms=session.duration_ms,
            )
            print(f"{label} -> uploaded {recording_id}")
        except Exception as error:                      # noqa: BLE001 - keep going
            failures += 1
            print(f"{label} -> FAILED: {error}")

    if failures:
        print(f"{failures} session(s) failed. The card file is untouched; re-run to retry.")
        return 1
    return 0


def main(argv=None) -> int:
    return run(build_parser().parse_args(argv))


if __name__ == "__main__":
    sys.exit(main())
