# syntax=docker/dockerfile:1
# GPU-enabled transcription worker (WhisperX + pyannote).
# Requires the NVIDIA Container Toolkit on the host to expose the GPU.
#
# CUDA 12.8 + cuDNN 9 base: required for Blackwell GPUs (RTX 50-series, sm_120).
# The cu121/torch 2.5.1 stack only compiles kernels up to sm_90, so model load
# fails with "no kernel image is available for execution on the device" on a 5090.
FROM nvidia/cuda:12.8.1-cudnn-runtime-ubuntu22.04

ENV DEBIAN_FRONTEND=noninteractive PYTHONUNBUFFERED=1
# libpython3.10 is for torchcodec, which whisperx 3.8 pulls in: its native library links against
# libpython3.10.so.1.0, which the slim python3 package does not ship. Without it torchcodec fails to
# load and prints a stack of FFmpeg tracebacks on every start - harmless, since whisperx falls back to
# decoding through ffmpeg itself, but noisy enough to bury a real error.
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 python3-pip libpython3.10 ffmpeg git \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# CUDA-enabled torch first (cu128 wheels include sm_120 kernels), then the rest. torch 2.8 is what
# whisperx 3.8.6 asks for (`torch~=2.8.0`), and cu128 wheels exist for it - so the Blackwell support
# the whole base image exists for survives the upgrade. torchvision is new: whisperx 3.8 requires it.
# The pip cache mount keeps downloaded wheels on the BuildKit cache, so a rebuild of this
# (multi-GB) layer reinstalls from cache instead of re-downloading ~10 GB.
RUN --mount=type=cache,target=/root/.cache/pip \
    pip3 install torch==2.8.0 torchaudio==2.8.0 torchvision==0.23.0 \
    --index-url https://download.pytorch.org/whl/cu128

COPY requirements.txt ./
RUN --mount=type=cache,target=/root/.cache/pip \
    pip3 install -r requirements.txt

# whisperx 3.8.6 asks for ctranslate2>=4.5.0 rather than the <4.5.0 cap 3.3.1 had, so this is now a
# floor being met rather than a cap being forced. Still pinned exactly: a resolver free to choose
# 4.5.x would silently drop Blackwell (sm_120), which landed in 4.6.3 and is why this base image exists.
RUN --mount=type=cache,target=/root/.cache/pip \
    pip3 install --no-deps --force-reinstall ctranslate2==4.6.3

COPY . ./

CMD ["python3", "worker.py"]
