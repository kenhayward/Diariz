"""Environment-driven configuration for the transcription worker."""
import os


class Config:
    # Redis
    REDIS_URL = os.getenv("REDIS_URL", "redis://redis:6379/0")
    STREAM_KEY = os.getenv("STREAM_KEY", "transcription-jobs")
    # Second stream this worker also consumes: audio-concatenation merge jobs (shares the same group).
    MERGE_STREAM_KEY = os.getenv("MERGE_STREAM_KEY", "audio-merge-jobs")
    CONSUMER_GROUP = os.getenv("CONSUMER_GROUP", "workers")
    CONSUMER_NAME = os.getenv("CONSUMER_NAME", "worker-1")

    # MinIO / S3
    S3_ENDPOINT = os.getenv("S3_ENDPOINT", "http://minio:9000")
    S3_ACCESS_KEY = os.getenv("S3_ACCESS_KEY", "minioadmin")
    S3_SECRET_KEY = os.getenv("S3_SECRET_KEY", "minioadmin")
    S3_BUCKET = os.getenv("S3_BUCKET", "recordings")

    # API callback
    API_BASE_URL = os.getenv("API_BASE_URL", "http://api:8080")
    CALLBACK_SECRET = os.getenv("CALLBACK_SECRET", "change-me")

    # Models
    DEVICE = os.getenv("DEVICE", "cuda")  # "cuda" or "cpu"
    COMPUTE_TYPE = os.getenv("COMPUTE_TYPE", "float16")  # use "int8" on CPU
    WHISPER_MODEL = os.getenv("WHISPER_MODEL", "large-v3")
    BATCH_SIZE = int(os.getenv("BATCH_SIZE", "16"))
    HF_TOKEN = os.getenv("HF_TOKEN", "")  # required for pyannote diarization
    # Reject audio longer than this many seconds (protects the GPU worker from huge uploads).
    # 0 disables the cap. 14400 = 4 h, matching the API's default upload size limit.
    MAX_AUDIO_SECONDS = float(os.getenv("MAX_AUDIO_SECONDS", "14400"))

    # Speaker embeddings (voiceprints): per-speaker ECAPA vectors for identification.
    # Gated so it degrades gracefully when off (the API simply skips identification).
    SAMPLE_RATE = 16000  # whisperx.load_audio resamples to 16 kHz
    ENABLE_SPEAKER_EMBEDDINGS = os.getenv("ENABLE_SPEAKER_EMBEDDINGS", "1") not in ("0", "false", "False", "")
    EMBED_MODEL = os.getenv("EMBED_MODEL", "speechbrain/spkrec-ecapa-voxceleb")  # 192-d, Apache-2.0
    EMBED_MAX_SECONDS = float(os.getenv("EMBED_MAX_SECONDS", "30"))  # cap pooled audio per speaker
    EMBED_CACHE_DIR = os.getenv("EMBED_CACHE_DIR", "")  # speechbrain savedir (blank => default)


config = Config()
