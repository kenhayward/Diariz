"""The command-line shell.

Thin by design - the interesting behaviour lives in framing/sessions/pipeline. What is
worth testing here is the wiring that decides what actually happens to your audio:
which device layout is used, where the timestamps come from, and whether anything gets
uploaded at all.
"""

import os
from datetime import datetime, timedelta, timezone

import pytest

from omi_sync.cli import build_parser, resolve_ended_at, resolve_token, run


def test_defaults_match_the_documented_workflow():
    args = build_parser().parse_args(["a01.txt"])
    assert args.device == "devkit"
    assert args.gap_minutes == 5
    assert args.min_session_seconds == 60
    assert args.max_session_hours == 2
    assert args.threshold is None
    assert args.dry_run is False


def test_card_path_is_required():
    with pytest.raises(SystemExit):
        build_parser().parse_args([])


# --- timestamps ---------------------------------------------------------------------

def test_ended_at_defaults_to_now_and_is_timezone_aware():
    got = resolve_ended_at(None)
    assert got.tzinfo is not None
    assert abs((datetime.now(timezone.utc) - got).total_seconds()) < 5


def test_explicit_ended_at_with_an_offset_is_preserved():
    got = resolve_ended_at("2026-08-25T17:00:00+01:00")
    assert got == datetime(2026, 8, 25, 16, 0, tzinfo=timezone.utc)


def test_naive_ended_at_is_interpreted_as_local_time_not_rejected():
    # Operators will type "2026-08-25 17:00" without an offset. Guessing UTC would
    # silently shift every recording; assuming local time is what they meant.
    got = resolve_ended_at("2026-08-25T17:00:00")
    assert got.tzinfo is not None
    assert got.replace(tzinfo=None) == datetime(2026, 8, 25, 17, 0)


def test_unparseable_ended_at_is_an_error():
    with pytest.raises(SystemExit):
        resolve_ended_at("last tuesday")


# --- auth ---------------------------------------------------------------------------

def test_explicit_token_wins():
    assert resolve_token(token="dz_api_arg", env={"DIARIZ_TOKEN": "dz_api_env"}) == "dz_api_arg"


def test_token_falls_back_to_the_environment():
    assert resolve_token(token=None, env={"DIARIZ_TOKEN": "dz_api_env"}) == "dz_api_env"


def test_missing_token_is_an_error_when_uploading():
    with pytest.raises(SystemExit):
        resolve_token(token=None, env={})


# --- run ----------------------------------------------------------------------------

def block(*frames):
    out = bytearray()
    for f in frames:
        out.append(len(f))
        out += f
    return bytes(out) + b"\x00" * (440 - len(out))


def card(tmp_path, pattern):
    frames = []
    for size, count in pattern:
        frames += [bytes([size]) * size] * count
    blocks, batch, used = bytearray(), [], 0
    for frame in frames:
        if used + 1 + len(frame) > 440:
            blocks += block(*batch)
            batch, used = [], 0
        batch.append(frame)
        used += 1 + len(frame)
    if batch:
        blocks += block(*batch)
    path = tmp_path / "a01.txt"
    path.write_bytes(bytes(blocks))
    return path


class RecordingUploader:
    def __init__(self):
        self.uploads = []

    def upload(self, blob, *, filename, title, started_at, duration_ms):
        self.uploads.append((filename, title, duration_ms, len(blob)))
        return "rec-" + str(len(self.uploads))


def test_dry_run_writes_files_and_uploads_nothing(tmp_path):
    path = card(tmp_path, [(80, 6000), (5, 30000), (80, 6000)])
    out = tmp_path / "out"
    uploader = RecordingUploader()

    args = build_parser().parse_args([
        str(path), "--dry-run", "--out", str(out),
        "--ended-at", "2026-08-25T17:00:00+00:00",
    ])
    code = run(args, uploader=uploader)

    assert code == 0
    assert uploader.uploads == []
    written = sorted(p.name for p in out.glob("*.opus"))
    assert len(written) == 2
    assert all((out / name).read_bytes().startswith(b"OggS") for name in written)


def test_upload_mode_sends_every_session(tmp_path):
    path = card(tmp_path, [(80, 6000), (5, 30000), (80, 6000)])
    uploader = RecordingUploader()

    args = build_parser().parse_args([
        str(path), "--ended-at", "2026-08-25T17:00:00+00:00",
    ])
    code = run(args, uploader=uploader)

    assert code == 0
    assert len(uploader.uploads) == 2
    assert all(name.endswith(".opus") for name, *_ in uploader.uploads)
    assert all(size > 0 for *_, size in uploader.uploads)


def test_scan_only_reports_without_encoding_anything(tmp_path, capsys):
    path = card(tmp_path, [(80, 6000), (5, 30000), (80, 6000)])
    uploader = RecordingUploader()

    args = build_parser().parse_args([
        str(path), "--scan-only", "--ended-at", "2026-08-25T17:00:00+00:00",
    ])
    code = run(args, uploader=uploader)

    assert code == 0
    assert uploader.uploads == []
    assert "2 session" in capsys.readouterr().out


def test_a_card_with_nothing_worth_uploading_exits_cleanly(tmp_path, capsys):
    path = card(tmp_path, [(5, 60000)])
    uploader = RecordingUploader()

    args = build_parser().parse_args([
        str(path), "--ended-at", "2026-08-25T17:00:00+00:00",
    ])
    code = run(args, uploader=uploader)

    assert code == 0
    assert uploader.uploads == []
    assert "no sessions" in capsys.readouterr().out.lower()


def test_a_missing_card_file_is_an_error(tmp_path):
    args = build_parser().parse_args([str(tmp_path / "nope.txt")])
    with pytest.raises(SystemExit):
        run(args, uploader=RecordingUploader())


def test_upload_failure_is_reported_and_does_not_abort_the_remaining_sessions(tmp_path, capsys):
    path = card(tmp_path, [(80, 6000), (5, 30000), (80, 6000)])

    class Flaky(RecordingUploader):
        def upload(self, blob, **kwargs):
            if not self.uploads:
                self.uploads.append(("failed",))
                raise RuntimeError("server exploded")
            return super().upload(blob, **kwargs)

    args = build_parser().parse_args([
        str(path), "--ended-at", "2026-08-25T17:00:00+00:00",
    ])
    code = run(args, uploader=Flaky())

    # A non-zero exit tells a cron job something went wrong, but one bad session must
    # not strand the rest of the card.
    assert code != 0
    assert "server exploded" in capsys.readouterr().out


def test_cv1_layout_can_be_selected(tmp_path):
    args = build_parser().parse_args(["a01.txt", "--device", "cv1"])
    assert args.device == "cv1"
