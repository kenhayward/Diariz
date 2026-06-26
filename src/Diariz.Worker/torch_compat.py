"""Compatibility shim for the torch>=2.6 `torch.load` default change.

torch 2.6 flipped `torch.load`'s `weights_only` argument default from False to
True. The pyannote 3.3.2 / whisperx 3.3.1 model checkpoints (VAD, segmentation,
diarization) embed non-tensor globals such as `omegaconf.listconfig.ListConfig`,
which the strict `weights_only=True` unpickler refuses with:

    _pickle.UnpicklingError: Weights only load failed ... Unsupported global

These are the official Hugging Face model files, loaded exactly as they were
before the CUDA 12.8 / torch 2.7 upgrade, so restoring the pre-2.6 default of
`weights_only=False` for them is safe. Doing it via the default (rather than
`add_safe_globals`) resolves every embedded global at once instead of
allowlisting them one error at a time.
"""
import logging

log = logging.getLogger("torch_compat")

_patched = False


def _default_weights_only_wrapper(orig_load, default=False):
    """Wrap `orig_load` so `weights_only` falls back to `default` when the caller
    leaves it unset OR passes it as None. PyTorch Lightning's `cloud_io._load`
    forwards `weights_only=None` to `torch.load`, and torch>=2.6 treats None as
    True — so None must be coerced, not just the missing key. An explicit,
    non-None caller value (e.g. True) is preserved."""
    def _load(*args, **kwargs):
        if kwargs.get("weights_only") is None:
            kwargs["weights_only"] = default
        return orig_load(*args, **kwargs)
    return _load


def restore_legacy_torch_load():
    """Patch `torch.load` to default `weights_only=False` (pre-2.6 behaviour).
    Idempotent and a no-op on torch<2.6 where the default is already False."""
    global _patched
    if _patched:
        return
    import torch  # deferred: torch is GPU-only and absent in the test env

    torch.load = _default_weights_only_wrapper(torch.load, default=False)
    _patched = True
    log.info("Patched torch.load to default weights_only=False (torch>=2.6 compat)")
