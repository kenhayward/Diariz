"""ROCm environment hygiene, applied before torch initialises the HSA runtime.

`HSA_OVERRIDE_GFX_VERSION` makes the ROCm runtime pretend the GPU is a different architecture (e.g.
`11.0.0` = gfx1100), which is both an escape hatch for missing kernels and, on gfx1151, a measured
speed-up. But the runtime distinguishes *unset* from *set to an empty string*, and an empty value makes
it fail to enumerate the GPU at all - `torch.cuda.is_available()` flips to False and the worker silently
falls back to CPU.

That is not a hypothetical: `deploy/docker-compose.rocm.yml` passes
`HSA_OVERRIDE_GFX_VERSION: ${HSA_OVERRIDE_GFX_VERSION:-}` and `.env.example` ships the key present but
empty, so the *documented* deployment path hands the container an empty value. Measured on a Ryzen
AI Max+ 395 / Radeon 8060S with the same image:

    (no variable)                      torch.cuda.is_available() -> True
    HSA_OVERRIDE_GFX_VERSION=          torch.cuda.is_available() -> False

Import this module before anything that imports torch.
"""
import logging

log = logging.getLogger("rocm_env")

GFX_OVERRIDE = "HSA_OVERRIDE_GFX_VERSION"


def clean_gfx_override(env=None) -> None:
    """Drop `HSA_OVERRIDE_GFX_VERSION` if it is empty/whitespace, and trim it otherwise.

    Mutates `env` in place (defaults to `os.environ`). Idempotent.
    """
    if env is None:
        import os
        env = os.environ

    if GFX_OVERRIDE not in env:
        return

    value = (env[GFX_OVERRIDE] or "").strip()
    if not value:
        # Deleting is what restores GPU detection - setting it to "" is the broken state.
        del env[GFX_OVERRIDE]
        # WARNING, not INFO, and deliberately so: this runs before logging.basicConfig, where Python's
        # handler-of-last-resort only prints WARNING and above. A silent correction to a silently
        # GPU-disabling misconfiguration is the worst of both worlds.
        log.warning("Unset empty %s (an empty value disables ROCm GPU detection)", GFX_OVERRIDE)
        return

    if value != env[GFX_OVERRIDE]:
        env[GFX_OVERRIDE] = value
    log.info("Using %s=%s", GFX_OVERRIDE, value)
