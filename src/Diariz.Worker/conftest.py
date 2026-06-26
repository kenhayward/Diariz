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
