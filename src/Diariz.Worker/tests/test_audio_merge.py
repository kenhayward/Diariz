"""Tests for the pure ffmpeg-command building in audio_merge."""
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
