"""E0: production ECAPA embeddings for every identification clip, through the worker's own code path.

Runs in the worker image (diariz-spike-worker) with this file mounted, and calls `pipeline._get_embedder()`
- the same SpeechBrain model, device and loading the worker uses - so E0 is production, not a re-creation.

Writes /data/vp/emb/ecapa.json: {"P001/rec003": [192 floats], ...}.
"""
import json
import sys
from pathlib import Path

import soundfile as sf

sys.path.insert(0, "/app")
import pipeline  # noqa: E402


def main():
    root = Path("/data/vp")
    embed = pipeline._get_embedder()
    out = {}
    for clip in sorted((root / "clips").glob("*/*.wav")):
        wav, sr = sf.read(clip, dtype="float32")
        assert sr == 16000
        out[f"{clip.parent.name}/{clip.stem}"] = [float(x) for x in embed(wav)]
    (root / "emb").mkdir(exist_ok=True)
    (root / "emb" / "ecapa.json").write_text(json.dumps(out))
    print(f"ecapa: {len(out)} embeddings, dim {len(next(iter(out.values())))}")


if __name__ == "__main__":
    main()
