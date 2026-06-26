"""Tests for the torch>=2.6 `torch.load` compatibility shim.

torch 2.6 flipped `torch.load`'s `weights_only` default from False to True. The
pyannote 3.3.2 / whisperx 3.3.1 checkpoints embed non-tensor globals (e.g.
omegaconf ListConfig) that the strict loader refuses, so the worker restores the
old default. The wrapper logic is pure (no real torch) so it is tested here
without the GPU stack installed.
"""
from torch_compat import _default_weights_only_wrapper


def test_wrapper_defaults_weights_only_to_false_when_absent():
    captured = {}

    def fake_load(*args, **kwargs):
        captured.update(kwargs)
        return "loaded"

    wrapped = _default_weights_only_wrapper(fake_load, default=False)
    result = wrapped("checkpoint.bin", map_location="cpu")

    assert result == "loaded"
    assert captured["weights_only"] is False
    assert captured["map_location"] == "cpu"


def test_wrapper_treats_explicit_none_as_absent():
    # PyTorch Lightning's cloud_io._load passes `weights_only=None` through to
    # torch.load, and torch>=2.6 treats None as True. The shim must coerce None
    # to the restored default, not leave it for torch to reject.
    captured = {}

    def fake_load(*args, **kwargs):
        captured.update(kwargs)

    wrapped = _default_weights_only_wrapper(fake_load, default=False)
    wrapped("checkpoint.bin", map_location="cpu", weights_only=None)

    assert captured["weights_only"] is False


def test_wrapper_preserves_explicit_weights_only():
    captured = {}

    def fake_load(*args, **kwargs):
        captured.update(kwargs)

    wrapped = _default_weights_only_wrapper(fake_load, default=False)
    wrapped("checkpoint.bin", weights_only=True)

    # An explicit, non-None caller choice must win over the restored default.
    assert captured["weights_only"] is True
