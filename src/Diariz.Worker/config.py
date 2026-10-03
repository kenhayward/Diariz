"""Environment-driven configuration for the transcription worker."""
import os
from urllib.parse import urlsplit, urlunsplit


def redact_url(url: str) -> str:
    """The URL with any password replaced by ***, for logging. REDIS_URL carries the Redis password
    (redis://:<password>@redis:6379/0), and the startup wait loop logs where it is connecting to."""
    try:
        parts = urlsplit(url)
        if parts.password is not None:
            user = parts.username or ""
            host = parts.netloc.rsplit("@", 1)[1]
            return urlunsplit(parts._replace(netloc=f"{user}:***@{host}"))
    except ValueError:
        pass
    # Anything unparseable that still looks like it holds credentials is not worth the risk of printing.
    return "<redacted>" if "@" in url else url



class Config:
    # Redis
    REDIS_URL = os.getenv("REDIS_URL", "redis://redis:6379/0")
    STREAM_KEY = os.getenv("STREAM_KEY", "transcription-jobs")
    # Second stream this worker also consumes: audio-concatenation merge jobs (shares the same group).
    MERGE_STREAM_KEY = os.getenv("MERGE_STREAM_KEY", "audio-merge-jobs")
    # On-demand voiceprint re-embeds. Third stream on this worker; needs only the ECAPA embedder, so
    # it is seconds of work - but it shares this process, so it can queue behind a transcription.
    VOICEPRINT_STREAM_KEY = os.getenv("VOICEPRINT_STREAM_KEY", "voiceprint-jobs")
    # Fourth stream: chunks of a capture still in progress, transcribed while the meeting runs. Read
    # ahead of the others (see worker.run_loop) - a live chunk queued behind an hour of audio would
    # arrive long after the meeting it belongs to had ended.
    LIVE_CHUNK_STREAM_KEY = os.getenv("LIVE_CHUNK_STREAM_KEY", "live-chunk-jobs")
    # Consume ONLY live chunks, ignoring full transcriptions, merges and voiceprints.
    #
    # The general worker already reads the live stream first, which bounds live latency by the job
    # already running rather than by queue depth - but that job can be a 60-minute transcription,
    # measured at roughly 75 s. A second container in this mode removes that tail: whichever worker is
    # free takes the chunk, and a busy general worker is simply not reading.
    #
    # Same CONSUMER_GROUP as the general worker on purpose, so Redis hands each chunk to exactly one of
    # them. The cost is a second copy of the model weights in VRAM, which is not free on a small card.
    LIVE_ONLY = os.getenv("LIVE_ONLY", "").strip().lower() in ("1", "true", "yes")

    CONSUMER_GROUP = os.getenv("CONSUMER_GROUP", "workers")
    CONSUMER_NAME = os.getenv("CONSUMER_NAME", "worker-1")

    # S3 store (SeaweedFS in compose; any S3-compatible endpoint works)
    S3_ENDPOINT = os.getenv("S3_ENDPOINT", "http://s3:8333")
    S3_ACCESS_KEY = os.getenv("S3_ACCESS_KEY", "minioadmin")
    S3_SECRET_KEY = os.getenv("S3_SECRET_KEY", "minioadmin")
    S3_BUCKET = os.getenv("S3_BUCKET", "recordings")

    # API callback
    API_BASE_URL = os.getenv("API_BASE_URL", "http://api:8080")
    CALLBACK_SECRET = os.getenv("CALLBACK_SECRET", "change-me")

    # Models
    DEVICE = os.getenv("DEVICE", "cuda")  # "cuda" or "cpu" (PyTorch-ROCm also reports "cuda" for AMD GPUs)
    COMPUTE_TYPE = os.getenv("COMPUTE_TYPE", "float16")  # use "int8" on CPU (faster-whisper/CTranslate2 only)
    # Whisper ASR backend. "whisperx" = faster-whisper (CTranslate2, CUDA/CPU — the default). "whisper" =
    # openai-whisper (pure PyTorch) for AMD ROCm, where CTranslate2 has no GPU support. Alignment,
    # diarization and voiceprints are PyTorch and run on either backend unchanged.
    ASR_BACKEND = os.getenv("ASR_BACKEND", "whisperx")  # "whisperx" | "whisper"
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
    # The pyannote pipeline used for diarization. community-1 was measured at the same speed as 3.1
    # under pyannote 4 (2.39s vs 2.34s on a 60 s window), so 3.1 is kept as the known quantity -
    # the 15x came from the pyannote/torch upgrade, not from changing pipeline.
    DIARIZATION_MODEL = os.getenv("DIARIZATION_MODEL", "pyannote/speaker-diarization-3.1")
    EMBED_MODEL = os.getenv("EMBED_MODEL", "speechbrain/spkrec-ecapa-voxceleb")  # 192-d, Apache-2.0
    # Cap on pooled audio per speaker. Raised from 30 s so a hand-picked selection is actually used;
    # ECAPA on 120 s vs 30 s costs a rounding error on GPU and the vectors stay comparable with centroids
    # built at 30 s. One cap for both the transcription-time and on-demand paths, so they cannot drift.
    EMBED_MAX_SECONDS = float(os.getenv("EMBED_MAX_SECONDS", "120"))
    EMBED_CACHE_DIR = os.getenv("EMBED_CACHE_DIR", "")  # speechbrain savedir (blank => default)

    # Split a Whisper segment where the word-level speaker changes (segment_split, issue #803), instead
    # of showing every word under the segment's majority speaker. Measured on AMI, words under the wrong
    # speaker fell from 8.6% to 6.4% (test) and 12.9% to 9.3% (dev), for 13-17% more segments.
    SPLIT_SEGMENTS_BY_WORD_SPEAKER = os.getenv("SPLIT_SEGMENTS_BY_WORD_SPEAKER", "1") not in (
        "0", "false", "False", "")
    # The live path separately, and off by default: its per-label voiceprints drive cross-chunk speaker
    # stitching, so it is turned on only once a replay of real chunks shows the stitcher unaffected.
    SPLIT_LIVE_SEGMENTS_BY_WORD_SPEAKER = os.getenv("SPLIT_LIVE_SEGMENTS_BY_WORD_SPEAKER", "0") not in (
        "0", "false", "False", "")
    # A run of another speaker's words inside one speaker's turn is absorbed when it has fewer words than
    # this AND lasts less than SPLIT_MIN_MS. Runs at a segment's edge are never absorbed.
    SPLIT_MIN_WORDS = int(os.getenv("SPLIT_MIN_WORDS", "2"))
    SPLIT_MIN_MS = float(os.getenv("SPLIT_MIN_MS", "1000"))


config = Config()
