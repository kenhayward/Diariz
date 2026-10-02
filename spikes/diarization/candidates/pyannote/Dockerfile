# Build from spikes/diarization/candidates:  docker build -f pyannote/Dockerfile -t diariz-spike-pyannote .
# Same torch line as the production worker (2.8 + cu128), and the same pyannote.audio (4.0.7).
FROM pytorch/pytorch:2.8.0-cuda12.8-cudnn9-runtime

ENV PYTHONUNBUFFERED=1 PYANNOTE_METRICS_ENABLED=0
RUN pip install --no-cache-dir "pyannote.audio==4.0.7" soundfile nvidia-ml-py

WORKDIR /app
COPY common/ common/
COPY pyannote/ pyannote/
CMD ["python", "pyannote/run.py"]
