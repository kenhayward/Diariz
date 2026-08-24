"""Re-embed a voiceprint from chosen spans of a recording's audio.

Pure with respect to the model - the embedder is passed in - so the slicing and the cap can be tested
without torch, exactly like pipeline._speaker_embeddings.
"""
import numpy as np

from config import config


def embed_spans(audio, spans, embed_fn, sample_rate: int = config.SAMPLE_RATE,
                max_seconds: float = config.EMBED_MAX_SECONDS):
    """Concatenate the audio inside `spans`, cap it, embed it, and L2-normalise.

    An empty `spans` means the whole clip - the state every sample that predates span selection is in,
    matching the API column's null. Spans are clamped to the clip, so a selection made before an audio
    merge cannot read past the end.

    Returns None when the spans select no audio at all. Embedding silence and storing it as someone's
    voiceprint is worse than reporting that there was nothing to embed: it would quietly poison the
    person's centroid rather than failing visibly.

    Returns {"Embedding": [...], "UsedMs": int, "SelectedMs": int}. UsedMs is what was actually pooled
    after the cap, SelectedMs what the user asked for - the UI states both rather than implying the whole
    selection was used.
    """
    n = len(audio)
    max_samples = int(max_seconds * sample_rate) if max_seconds else n

    if not spans:
        selected_ms = int(round(n / sample_rate * 1000))
        chunks, total = [audio], n
    else:
        chunks, total, selected_ms = [], 0, 0
        for span in spans:
            start_ms, end_ms = int(span["StartMs"]), int(span["EndMs"])
            selected_ms += max(0, end_ms - start_ms)
            a = max(0, int(start_ms * sample_rate / 1000))
            b = min(n, int(end_ms * sample_rate / 1000))
            if b <= a:
                continue
            chunks.append(audio[a:b])
            total += b - a
            if total >= max_samples:
                break

    if not chunks or total == 0:
        return None

    waveform = np.concatenate(chunks)[:max_samples]
    if waveform.size == 0:
        return None

    vec = np.asarray(embed_fn(waveform), dtype="float32").reshape(-1)
    norm = float(np.linalg.norm(vec))
    if norm > 0:
        vec = vec / norm

    return {
        "Embedding": [float(x) for x in vec],
        "UsedMs": int(round(waveform.size / sample_rate * 1000)),
        "SelectedMs": selected_ms,
    }
