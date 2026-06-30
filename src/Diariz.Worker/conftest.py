"""Shared pytest setup for the worker tests.

Worker modules (config, callback, worker, pipeline, storage) are top-level modules
that live next to this file, so put this directory on the import path. `whisperx`
(and its torch/CUDA dependencies) is GPU-only and not installed in the test env;
`pipeline.py` imports it at module load, so stub it before any test imports the
worker modules. The logic under test never calls into the real models.
"""
import os
import sys
from unittest.mock import MagicMock

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

sys.modules.setdefault("whisperx", MagicMock())
# speechbrain (+torch) are imported lazily inside pipeline._get_embedder(), which the
# tests never call (they pass a stub embedder), so a top-level stub is enough for safety.
sys.modules.setdefault("speechbrain", MagicMock())
# openai-whisper (the ROCm ASR backend) is imported lazily inside pipeline._get_whisper_py();
# stub it so the ASR-dispatch tests can drive it without torch installed.
sys.modules.setdefault("whisper", MagicMock())
