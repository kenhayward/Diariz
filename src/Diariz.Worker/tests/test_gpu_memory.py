"""Tests for gpu_memory.release() - handing a finished job's cached GPU memory back (issue #782)."""
import sys
import types

import gpu_memory


def _fake_torch(calls, cuda_available=True):
    torch = types.ModuleType("torch")
    torch.cuda = types.SimpleNamespace(
        is_available=lambda: cuda_available,
        empty_cache=lambda: calls.append("empty_cache"),
    )
    return torch


def test_release_collects_garbage_then_empties_the_cuda_cache(monkeypatch):
    # Order matters: the allocator can only hand back blocks no live tensor still holds, so the job's
    # unreachable tensors must be collected first.
    calls = []
    monkeypatch.setitem(sys.modules, "torch", _fake_torch(calls))
    monkeypatch.setattr(gpu_memory.gc, "collect", lambda: calls.append("gc"))

    gpu_memory.release()

    assert calls == ["gc", "empty_cache"]


def test_release_leaves_the_cuda_cache_alone_without_a_gpu(monkeypatch):
    calls = []
    monkeypatch.setitem(sys.modules, "torch", _fake_torch(calls, cuda_available=False))
    monkeypatch.setattr(gpu_memory.gc, "collect", lambda: calls.append("gc"))

    gpu_memory.release()

    assert calls == ["gc"]


def test_release_is_a_no_op_when_torch_is_not_installed(monkeypatch):
    # The test environment (and a CPU-only image) has no torch; release must never be the thing that fails.
    monkeypatch.setitem(sys.modules, "torch", None)  # makes `import torch` raise ImportError

    gpu_memory.release()


def test_release_never_raises_even_if_the_cache_call_fails(monkeypatch):
    # It runs in run_loop's finally, after the job has already been reported - an error here would take
    # the worker down for the sake of housekeeping.
    torch = types.ModuleType("torch")

    def boom():
        raise RuntimeError("CUDA error: device lost")

    torch.cuda = types.SimpleNamespace(is_available=lambda: True, empty_cache=boom)
    monkeypatch.setitem(sys.modules, "torch", torch)

    gpu_memory.release()
