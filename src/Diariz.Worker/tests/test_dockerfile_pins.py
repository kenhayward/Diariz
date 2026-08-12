"""The worker images must pin their base image to an immutable tag.

`rocm/pytorch:latest` drifted to ROCm 7.14 / torch 2.13 / torchaudio 2.11, and
torchaudio 2.11 removed the top-level `AudioMetaData` and `info` symbols that
pyannote.audio 3.3.2 annotates with in `core/io.py`. The result was an
ImportError at `import whisperx`, so every ROCm worker container crash-looped and
no job could be transcribed. Nothing in the repo changed - the moving tag did.

The Dockerfile already carried a comment warning that `latest` is a moving
target; this test turns that comment into something CI enforces.
"""
import re
from pathlib import Path

import pytest

WORKER_DIR = Path(__file__).resolve().parent.parent

# A floating tag is any of these: no tag at all (implicitly `latest`), an
# explicit `:latest`, or a rolling channel alias.
FLOATING_TAGS = {"latest", "main", "master", "nightly", "edge", "dev", "stable"}

DOCKERFILES = ["Dockerfile", "Dockerfile.rocm"]


def _from_lines(dockerfile: Path):
    """Yield the image reference of every FROM instruction, ignoring the build
    stage aliases that `FROM x AS y` introduces."""
    stages = set()
    for raw in dockerfile.read_text().splitlines():
        line = raw.strip()
        if not re.match(r"(?i)^FROM\s", line):
            continue
        parts = line.split()
        image = parts[1]
        if len(parts) >= 4 and parts[2].lower() == "as":
            stages.add(parts[3])
        # `FROM builder` referring to an earlier stage is not a registry pull.
        if image in stages:
            continue
        yield image


@pytest.mark.parametrize("name", DOCKERFILES)
def test_base_images_are_pinned(name):
    dockerfile = WORKER_DIR / name
    if not dockerfile.exists():
        pytest.skip(f"{name} not present")

    images = list(_from_lines(dockerfile))
    assert images, f"{name} has no FROM instruction"

    for image in images:
        # Strip a registry host's port before looking for the tag separator.
        ref = image.rsplit("/", 1)[-1]
        assert ":" in ref or "@" in image, (
            f"{name}: base image {image!r} has no tag - it resolves to :latest, "
            "which silently drifts the whole torch/torchaudio stack."
        )
        if "@" in image:  # a digest pin is immutable by definition
            continue
        tag = ref.split(":", 1)[1]
        assert tag.lower() not in FLOATING_TAGS, (
            f"{name}: base image {image!r} uses the floating tag {tag!r}. "
            "Pin an explicit release tag so the torch/torchaudio versions the "
            "pyannote/whisperx pins were validated against cannot move."
        )
