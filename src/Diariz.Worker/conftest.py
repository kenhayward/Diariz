"""Shared pytest setup for the worker tests.

Worker modules (config, callback, worker, pipeline, storage) are top-level modules
that live next to this file, so put this directory on the import path. `whisperx`
(and its torch/CUDA dependencies) is GPU-only and not installed in the test env;
`pipeline.py` imports it at module load, so stub it before any test imports the
worker modules. The logic under test never calls into the real models.
"""
import os
import sys
import types
from unittest.mock import MagicMock

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))


class _StubModule(types.ModuleType):
    """A stand-in module whose attributes appear on demand AND stay in __dict__.

    A bare MagicMock looks equivalent, but it is not: its auto-created children live in
    _mock_children, never in __dict__. Since pytest 9.2, monkeypatch.setattr reads the old
    value out of target.__dict__ (the fix for pytest #10644) and so records "absent" for a
    mock attribute, which makes undo() `delattr` it at the end of the test. Deleting an
    attribute on a mock is sticky - every later access raises AttributeError - so the stub
    is poisoned for the rest of the session and the second test to patch that name fails
    with "has no attribute". Memoising into __dict__ here gives monkeypatch a real old
    value to put back, so patching the same name in test after test works.
    """

    def __getattr__(self, name):
        if name.startswith("__") and name.endswith("__"):
            raise AttributeError(name)
        value = MagicMock(name=f"{self.__name__}.{name}")
        setattr(self, name, value)
        return value


sys.modules.setdefault("whisperx", _StubModule("whisperx"))
# speechbrain (+torch) are imported lazily inside pipeline._get_embedder(), which the
# tests never call (they pass a stub embedder), so a top-level stub is enough for safety.
sys.modules.setdefault("speechbrain", _StubModule("speechbrain"))
# openai-whisper (the ROCm ASR backend) is imported lazily inside pipeline._get_whisper_py();
# stub it so the ASR-dispatch tests can drive it without torch installed.
sys.modules.setdefault("whisper", _StubModule("whisper"))
