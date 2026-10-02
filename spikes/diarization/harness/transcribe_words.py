"""Word timings for the word-level metric, from the production worker's own ASR + alignment.

Runs inside the worker image (src/Diariz.Worker built as diariz-spike-worker) with this file mounted,
so the segments and word boundaries are exactly what production would produce for the same audio:

  docker run --rm --gpus all -e SET=eval -v "$DATA:/data" -v .../transcribe_words.py:/spike/transcribe_words.py \
    diariz-spike-worker python /spike/transcribe_words.py

Writes /data/<SET>/words/<uri>.json: [{"s", "e", "words": [{"s", "e"}]}] in seconds. Timings only - no
text is kept, since the metric needs none. Words the aligner could not time are dropped, as in
pipeline._shape_words. All sets here are English, so the language is pinned (see pipeline._asr).
"""
import json
import os
import sys
from pathlib import Path

sys.path.insert(0, "/app")
import whisperx  # noqa: E402

from config import config  # noqa: E402
import pipeline  # noqa: E402


def main():
    root = Path("/data") / os.getenv("SET", "eval")
    out = root / "words"
    out.mkdir(exist_ok=True)
    align_model, metadata = pipeline._get_align("en")
    for wav in sorted((root / "audio").glob("*.wav")):
        target = out / f"{wav.stem}.json"
        if target.exists():
            continue
        audio = whisperx.load_audio(str(wav))
        asr = pipeline._asr(audio, "en")
        aligned = whisperx.align(asr["segments"], align_model, metadata, audio, config.DEVICE,
                                 return_char_alignments=False)
        segments = []
        for seg in aligned["segments"]:
            words = [{"s": round(w["start"], 3), "e": round(w["end"], 3)}
                     for w in seg.get("words") or [] if w.get("start") is not None and w.get("end") is not None]
            if words and (seg.get("text") or "").strip():
                segments.append({"s": round(seg["start"], 3), "e": round(seg["end"], 3), "words": words})
        target.write_text(json.dumps(segments))
        print(f"{wav.stem}: {len(segments)} segments, {sum(len(s['words']) for s in segments)} words", flush=True)


if __name__ == "__main__":
    main()
