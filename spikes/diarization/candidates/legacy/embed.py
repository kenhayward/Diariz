"""E4 Resemblyzer and E5 Vosk-spk embeddings for every identification clip -> /data/vp/emb/<name>.json.

Resemblyzer embeds the whole clip (its own VAD trims silence). Vosk returns one x-vector per recognised
utterance; those are averaged, weighted by the frames each covered, which is how its example compares
speakers over longer audio.
"""
import json
from pathlib import Path

import numpy as np
import soundfile as sf


def resemblyzer(clips):
    from resemblyzer import VoiceEncoder, preprocess_wav
    enc = VoiceEncoder("cpu")
    return {k: enc.embed_utterance(preprocess_wav(wav, source_sr=16000)) for k, wav in clips.items()}


def vosk(clips):
    from vosk import KaldiRecognizer, Model, SetLogLevel, SpkModel
    SetLogLevel(-1)
    model, spk = Model("/models/vosk-model-small-en-us-0.15"), SpkModel("/models/vosk-model-spk-0.4")
    out = {}
    for k, wav in clips.items():
        rec = KaldiRecognizer(model, 16000, spk)
        pcm = (np.clip(wav, -1, 1) * 32767).astype("<i2").tobytes()
        vecs, weights = [], []
        for i in range(0, len(pcm), 8000):
            if rec.AcceptWaveform(pcm[i:i + 8000]):
                r = json.loads(rec.Result())
                if "spk" in r:
                    vecs.append(r["spk"]); weights.append(r.get("spk_frames", 1))
        r = json.loads(rec.FinalResult())
        if "spk" in r:
            vecs.append(r["spk"]); weights.append(r.get("spk_frames", 1))
        if vecs:
            out[k] = np.average(np.array(vecs), axis=0, weights=np.array(weights, dtype=float))
    return out


def main():
    root = Path("/data/vp")
    clips = {f"{c.parent.name}/{c.stem}": sf.read(c, dtype="float32")[0] for c in sorted((root / "clips").glob("*/*.wav"))}
    (root / "emb").mkdir(exist_ok=True)
    for name, fn in (("resemblyzer", resemblyzer), ("vosk_spk", vosk)):
        emb = fn(clips)
        (root / "emb" / f"{name}.json").write_text(json.dumps({k: [float(x) for x in v] for k, v in emb.items()}))
        print(f"{name}: {len(emb)} embeddings, dim {len(next(iter(emb.values())))}", flush=True)


if __name__ == "__main__":
    main()
