# syntax=docker/dockerfile:1
# GPU-enabled transcription worker (WhisperX + pyannote).
# Requires the NVIDIA Container Toolkit on the host to expose the GPU.
#
# CUDA 12.8 + cuDNN 9 base: required for Blackwell GPUs (RTX 50-series, sm_120).
# The cu121/torch 2.5.1 stack only compiles kernels up to sm_90, so model load
# fails with "no kernel image is available for execution on the device" on a 5090.
FROM nvidia/cuda:12.8.1-cudnn-runtime-ubuntu22.04

ENV DEBIAN_FRONTEND=noninteractive PYTHONUNBUFFERED=1
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 python3-pip ffmpeg git \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# CUDA-enabled torch first (cu128 wheels include sm_120 kernels), then the rest.
# The pip cache mount keeps downloaded wheels on the BuildKit cache, so a rebuild of this
# (multi-GB) layer reinstalls from cache instead of re-downloading ~10 GB.
RUN --mount=type=cache,target=/root/.cache/pip \
    pip3 install torch==2.7.1 torchaudio==2.7.1 \
    --index-url https://download.pytorch.org/whl/cu128

COPY requirements.txt ./
RUN --mount=type=cache,target=/root/.cache/pip \
    pip3 install -r requirements.txt

# whisperx 3.3.1 conservatively pins ctranslate2<4.5.0, but Blackwell (sm_120)
# support only landed in ctranslate2 4.6.3 (CUDA 12.8; INT8 disabled on sm_120 so
# the worker's float16 compute_type is the supported path). Force past the cap.
RUN --mount=type=cache,target=/root/.cache/pip \
    pip3 install --no-deps --force-reinstall ctranslate2==4.6.3

COPY . ./

CMD ["python3", "worker.py"]
