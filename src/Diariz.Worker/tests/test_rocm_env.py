"""An empty HSA_OVERRIDE_GFX_VERSION must be removed, not passed to the ROCm runtime.

`deploy/docker-compose.rocm.yml` sets `HSA_OVERRIDE_GFX_VERSION: ${HSA_OVERRIDE_GFX_VERSION:-}` and
`.env.example` ships the key with an empty value, so on a default ROCm deployment the variable reaches
the container *defined but empty*. That is not the same as unset: measured on a Ryzen AI Max+ 395,

    docker run ... diariz-worker  python3 -c "import torch; torch.cuda.is_available()"   -> True
    docker run -e HSA_OVERRIDE_GFX_VERSION= ... (same command)                           -> False

so the GPU silently disappears and the worker falls back to CPU. Strip empty values before torch
initialises the HSA runtime.
"""
import rocm_env


def test_empty_value_is_removed():
    env = {"HSA_OVERRIDE_GFX_VERSION": "", "OTHER": "keep"}
    rocm_env.clean_gfx_override(env)
    assert "HSA_OVERRIDE_GFX_VERSION" not in env
    assert env["OTHER"] == "keep"


def test_whitespace_only_value_is_removed():
    env = {"HSA_OVERRIDE_GFX_VERSION": "  \t "}
    rocm_env.clean_gfx_override(env)
    assert "HSA_OVERRIDE_GFX_VERSION" not in env


def test_real_value_is_preserved_exactly():
    env = {"HSA_OVERRIDE_GFX_VERSION": "11.0.0"}
    rocm_env.clean_gfx_override(env)
    assert env["HSA_OVERRIDE_GFX_VERSION"] == "11.0.0"


def test_value_is_trimmed_so_a_stray_space_still_applies():
    # A trailing space in a .env line would otherwise be passed to the HSA runtime verbatim.
    env = {"HSA_OVERRIDE_GFX_VERSION": " 11.0.0 "}
    rocm_env.clean_gfx_override(env)
    assert env["HSA_OVERRIDE_GFX_VERSION"] == "11.0.0"


def test_absent_variable_is_left_absent():
    env = {}
    rocm_env.clean_gfx_override(env)
    assert env == {}


def test_defaults_to_the_process_environment(monkeypatch):
    monkeypatch.setenv("HSA_OVERRIDE_GFX_VERSION", "")
    rocm_env.clean_gfx_override()
    import os
    assert "HSA_OVERRIDE_GFX_VERSION" not in os.environ


def test_is_idempotent():
    env = {"HSA_OVERRIDE_GFX_VERSION": ""}
    rocm_env.clean_gfx_override(env)
    rocm_env.clean_gfx_override(env)
    assert "HSA_OVERRIDE_GFX_VERSION" not in env
