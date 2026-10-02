# Build from spikes/diarization/candidates:  docker build -f sherpa/Dockerfile -t diariz-spike-sherpa .
# CPU wheel of sherpa-onnx (ONNX Runtime inside). Models are the k2-fsa release assets, all Apache-2.0 or
# CC-BY-4.0 (TitaNet). The Reverb diarization models on the same release page are deliberately not used:
# their licence is non-commercial.
FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1
RUN apt-get update && apt-get install -y --no-install-recommends curl bzip2 && rm -rf /var/lib/apt/lists/*
RUN pip install --no-cache-dir "sherpa-onnx==1.13.8" soundfile numpy nvidia-ml-py

ARG REL=https://github.com/k2-fsa/sherpa-onnx/releases/download
WORKDIR /models
RUN curl -sSL $REL/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2 | tar xj \
 && curl -sSLO $REL/speaker-recongition-models/3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx \
 && curl -sSLO $REL/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx \
 && curl -sSLO $REL/speaker-recongition-models/nemo_en_titanet_large.onnx

WORKDIR /app
COPY common/ common/
COPY sherpa/ sherpa/
CMD ["python", "sherpa/run.py"]
