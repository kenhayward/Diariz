# Build from spikes/diarization/candidates:  docker build -f legacy/Dockerfile -t diariz-spike-legacy .
# E4 Resemblyzer (GE2E, 256-d) and E5 Vosk speaker model (x-vector), both Apache-2.0, both CPU.
# Vosk only emits speaker vectors alongside recognition, so it needs a (small) English ASR model too.
FROM python:3.11-slim

ENV PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1
RUN apt-get update && apt-get install -y --no-install-recommends build-essential curl unzip \
    && rm -rf /var/lib/apt/lists/*
RUN pip install torch --index-url https://download.pytorch.org/whl/cpu \
 && pip install resemblyzer vosk soundfile numpy "setuptools<81"

WORKDIR /models
RUN curl -sSLO https://alphacephei.com/vosk/models/vosk-model-spk-0.4.zip && unzip -q vosk-model-spk-0.4.zip \
 && curl -sSLO https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip && unzip -q vosk-model-small-en-us-0.15.zip \
 && rm *.zip

WORKDIR /app
COPY legacy/ legacy/
CMD ["python", "legacy/embed.py"]
