# CPU-only harness: builds the evaluation set from a platform backup and scores candidate output.
# The candidates themselves run in their own images under ../candidates.
FROM python:3.12-slim

RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir "psycopg[binary]==3.2.*" "pyannote.metrics==4.0.*" pytest

WORKDIR /harness
COPY . ./
