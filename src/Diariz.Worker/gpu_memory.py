"""Give the GPU memory a finished job used back to the device (issue #782).

PyTorch's caching allocator keeps every block a job allocated - alignment, diarization and the voiceprint
pass - reserved for reuse, for the life of the process. On a card shared with an LLM that is ~6 GB held
idle between jobs, and the LLM spills into system memory. Calling this after each job keeps only what is
still referenced: the lazily-loaded model weights in pipeline.py, which are cached on purpose.

Whisper itself runs on CTranslate2, which has its own allocator and already returns its peak; this is
about the PyTorch stages.
"""
import gc
import logging

log = logging.getLogger("gpu_memory")


def release() -> None:
    """Collect the finished job's unreachable tensors, then return the allocator's unused blocks to the
    device. Never raises: it is housekeeping in run_loop's ``finally``, after the job has been reported."""
    gc.collect()  # first - the allocator can only hand back blocks no live tensor still holds
    try:
        import torch
    except ImportError:
        return  # CPU-only image or the test environment: nothing to release
    try:
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except Exception:  # noqa: BLE001 - a failed release must not take the worker down
        log.warning("Could not release cached GPU memory", exc_info=True)
