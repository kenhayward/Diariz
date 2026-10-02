"""E1-E3 and extras: speaker embeddings for every identification clip via sherpa-onnx's ONNX models.

  python sherpa/embed.py       # all models below -> /data/vp/emb/<name>.json

Covers 3D-Speaker (CAM++, ERes2Net, ERes2NetV2), NVIDIA TitaNet-Large and WeSpeaker ResNet34. ERes2NetV2
is only published trained on Chinese (zh-cn, 200k speakers); it is included because the research table
lists it, and its result shows whether that training transfers to our English audio.
"""
import json
import sys
from pathlib import Path

import numpy as np
import sherpa_onnx
import soundfile as sf

MODELS = {
    "campplus_en": "3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx",
    "campplus_zh_en": "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx",
    "eres2net_en": "3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx",
    "eres2netv2_zh": "3dspeaker_speech_eres2netv2_sv_zh-cn_16k-common.onnx",
    "titanet_large": "nemo_en_titanet_large.onnx",
    "wespeaker_resnet34": "wespeaker_en_voxceleb_resnet34_LM.onnx",
}


# TitaNet's ONNX export cannot take a 120 s clip in one pass (a broadcast fails inside the encoder), so
# long clips are embedded in windows and the L2-normalised window embeddings averaged, weighted by length.
WINDOW_S = 30
WINDOWED = {"titanet_large"}


def embed(ex, wav, sr):
    vecs, weights = [], []
    for i in range(0, len(wav), WINDOW_S * sr):
        piece = wav[i:i + WINDOW_S * sr]
        if len(piece) < sr:  # under a second: too little to embed, and it would dilute the average
            continue
        stream = ex.create_stream()
        stream.accept_waveform(sr, piece)
        stream.input_finished()
        v = np.asarray(ex.compute(stream))
        vecs.append(v / (np.linalg.norm(v) or 1))
        weights.append(len(piece))
    return np.average(np.array(vecs), axis=0, weights=np.array(weights, dtype=float))


def main():
    root = Path("/data/vp")
    (root / "emb").mkdir(exist_ok=True)
    clips = sorted((root / "clips").glob("*/*.wav"))
    only = sys.argv[1:]
    for name, file in MODELS.items():
        if only and name not in only:
            continue
        ex = sherpa_onnx.SpeakerEmbeddingExtractor(
            sherpa_onnx.SpeakerEmbeddingExtractorConfig(model=f"/models/{file}", num_threads=16))
        out = {}
        for clip in clips:
            wav, sr = sf.read(clip, dtype="float32")
            if name in WINDOWED:
                v = embed(ex, wav, sr)
            else:
                stream = ex.create_stream()
                stream.accept_waveform(sr, wav)
                stream.input_finished()
                v = np.asarray(ex.compute(stream))
            out[f"{clip.parent.name}/{clip.stem}"] = [float(x) for x in v]
        (root / "emb" / f"{name}.json").write_text(json.dumps(out))
        print(f"{name}: {len(out)} embeddings, dim {ex.dim}", flush=True)


if __name__ == "__main__":
    main()
