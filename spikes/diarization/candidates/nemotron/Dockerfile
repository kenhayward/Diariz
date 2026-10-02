# Build from spikes/diarization/candidates:  docker build -f nemotron/Dockerfile -t diariz-spike-nemotron .
# NeMo needs Python 3.12+, and Nemotron-3 needs NeMo from main: the 3.0.0 release (2026-08-07) predates the
# rope attention it uses (added 2026-09-12). Pinned to a main commit for reproducibility. torch is installed first from the cu128 index (Blackwell-capable, same line as the
# worker) and then held there while NeMo resolves the rest.
FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1
RUN apt-get update && apt-get install -y --no-install-recommends libsndfile1 ffmpeg build-essential git \
    && rm -rf /var/lib/apt/lists/*
RUN pip install torch==2.8.0 torchaudio==2.8.0 --index-url https://download.pytorch.org/whl/cu128
RUN echo "torch==2.8.0" > /tmp/c.txt && echo "torchaudio==2.8.0" >> /tmp/c.txt \
 && pip install Cython packaging \
 && pip install -c /tmp/c.txt "nemo-toolkit[asr] @ git+https://github.com/NVIDIA-NeMo/NeMo.git@1688cc3d6a9ade854f544987810c53f605dc86fc" soundfile nvidia-ml-py

# NVIDIA's tuned post-processing settings (Apache-2.0, same pinned commit).
ARG PP=https://raw.githubusercontent.com/NVIDIA-NeMo/NeMo/1688cc3d6a9ade854f544987810c53f605dc86fc/examples/speaker_tasks/diarization/conf/post_processing
RUN mkdir -p /app/postproc && cd /app/postproc  && python -c "import urllib.request as u; [u.urlretrieve('$PP/'+f, f) for f in ('diar_streaming_sortformer_4spk-v2_dihard3-dev.yaml', 'diar_streaming_sortformer_4spk-v2_callhome-part1.yaml')]"

WORKDIR /app
COPY common/ common/
COPY nemotron/ nemotron/
CMD ["python", "nemotron/run.py"]
