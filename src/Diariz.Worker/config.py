"""Environment-driven configuration for the transcription worker."""
import os


class Config:
    # Redis
    REDIS_URL = os.getenv("REDIS_URL", "redis://redis:6379/0")
    STREAM_KEY = os.getenv("STREAM_KEY", "transcription-jobs")
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


config = Config()
