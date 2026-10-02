"""E1-E3 and extras: speaker embeddings for every identification clip via sherpa-onnx's ONNX models.

  python sherpa/embed.py       # all models below -> /data/vp/emb/<name>.json

Covers 3D-Speaker (CAM++, ERes2Net, ERes2NetV2), NVIDIA TitaNet-Large and WeSpeaker ResNet34. ERes2NetV2
is only published trained on Chinese (zh-cn, 200k speakers); it is included because the research table
lists it, and its result shows whether that training transfers to our English audio.
"""
import json
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


def main():
    root = Path("/data/vp")
    (root / "emb").mkdir(exist_ok=True)
    clips = sorted((root / "clips").glob("*/*.wav"))
    for name, file in MODELS.items():
        ex = sherpa_onnx.SpeakerEmbeddingExtractor(
            sherpa_onnx.SpeakerEmbeddingExtractorConfig(model=f"/models/{file}", num_threads=16))
        out = {}
        for clip in clips:
            wav, sr = sf.read(clip, dtype="float32")
            stream = ex.create_stream()
            stream.accept_waveform(sr, wav)
            stream.input_finished()
            out[f"{clip.parent.name}/{clip.stem}"] = [float(x) for x in np.asarray(ex.compute(stream))]
        (root / "emb" / f"{name}.json").write_text(json.dumps(out))
        print(f"{name}: {len(out)} embeddings, dim {ex.dim}", flush=True)


if __name__ == "__main__":
    main()
