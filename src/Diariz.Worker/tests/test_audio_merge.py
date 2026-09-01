"""Tests for the pure ffmpeg-command building and byte-joining in audio_merge."""
import os
import pytest

import audio_merge


def test_build_concat_command_uses_the_concat_filter_over_all_inputs():
    cmd = audio_merge.build_concat_command(["a.webm", "b.wav", "c.mp3"], "out.webm")

    assert cmd[0] == "ffmpeg"
    # Each input is passed with -i, in order.
    assert cmd.count("-i") == 3
    assert cmd[cmd.index("-i") + 1] == "a.webm"
    # The filter graph concatenates all three audio streams into one labelled output.
    fc = cmd[cmd.index("-filter_complex") + 1]
    assert fc == "[0:a][1:a][2:a]concat=n=3:v=0:a=1[out]"
    assert cmd[cmd.index("-map") + 1] == "[out]"
    assert "libopus" in cmd  # re-encode so heterogeneous inputs stitch cleanly
    assert cmd[-1] == "out.webm"


def test_build_concat_command_rejects_empty_input():
    with pytest.raises(ValueError):
        audio_merge.build_concat_command([], "out.webm")


def test_join_bytes_concatenates_in_order(tmp_path):
    """The live-capture path byte-joins chunks before ffmpeg ever sees them.

    Asserted on bytes rather than on a duration on purpose: only the first chunk carries the WebM
    header, so anything that opens the later ones to check has already failed - which is exactly the
    mistake this function exists to prevent (see the S0 findings in
    docs/Streaming_Capture_and_Live_Transcript.md section 5.1).
    """
    parts = []
    for i, body in enumerate([b"AAA", b"BB", b"CCCC"]):
        p = tmp_path / f"chunk-{i}.webm"
        p.write_bytes(body)
        parts.append(str(p))

    joined = audio_merge.join_bytes(parts)
    try:
        assert open(joined, "rb").read() == b"AAABBCCCC"
    finally:
        os.remove(joined)


def test_join_bytes_rejects_empty_input():
    with pytest.raises(ValueError):
        audio_merge.join_bytes([])


def test_join_then_concat_hands_ffmpeg_exactly_one_input(tmp_path, monkeypatch):
    """The regression guard for the S0 finding.

    build_concat_command opens every input independently, so passing the raw chunk list makes ffmpeg
    die on the second one with "EBML header parsing failed". The joined file must arrive as a single
    input.
    """
    parts = []
    for i in range(3):
        p = tmp_path / f"chunk-{i}.webm"
        p.write_bytes(b"x" * 8)
        parts.append(str(p))

    seen: dict = {}

    def fake_concat(paths):
        seen["paths"] = list(paths)
        return "/tmp/out.webm", 1234, 99

    monkeypatch.setattr(audio_merge, "concat", fake_concat)

    out, duration_ms, size = audio_merge.join_then_concat(parts)

    assert len(seen["paths"]) == 1, "ffmpeg must receive the joined file, not the raw chunks"
    assert (out, duration_ms, size) == ("/tmp/out.webm", 1234, 99)


CLUSTER_ID = bytes.fromhex("1F43B675")


def test_webm_init_segment_is_everything_before_the_first_cluster(tmp_path):
    """A live chunk after the first two cannot be decoded without this.

    Only fragment 0 of a MediaRecorder stream carries the WebM/EBML header, so joining chunk N onto
    chunk N-1 produces two headerless fragments and ffmpeg dies with "EBML header parsing failed".
    Prepending chunk 0's initialisation segment - the bytes up to its first Cluster, a few hundred of
    them - makes the pair decodable without dragging the whole first chunk's audio along with it.
    """
    src = tmp_path / "first.webm"
    src.write_bytes(b"\x1aE\xdf\xa3HEADERBYTES" + CLUSTER_ID + b"clusterpayload")

    init = audio_merge.webm_init_segment(str(src))

    assert init == b"\x1aE\xdf\xa3HEADERBYTES"
    assert CLUSTER_ID not in init


def test_webm_init_segment_of_a_headerless_fragment_is_empty(tmp_path):
    """A fragment that opens on a cluster has no init segment to give - say so rather than handing
    back the whole file, which would silently prepend a duplicate of somebody's audio."""
    src = tmp_path / "later.webm"
    src.write_bytes(CLUSTER_ID + b"payload only")

    assert audio_merge.webm_init_segment(str(src)) == b""
