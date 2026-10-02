"""sherpa-onnx offline diarization (D3): pyannote segmentation-3.0 (ONNX) + a speaker-embedding ONNX model +
agglomerative clustering. No torch at all.

  EMBED=campplus | resnet34 | titanet   (which embedding model; see the Dockerfile for the files)
  THRESHOLD=0.5                         clustering threshold - smaller means more speakers
  PROVIDER=cpu | cuda   THREADS=16

Runs on CPU by default: the spike's question for this candidate is whether a torch-free, VRAM-free
diarizer is good enough, and its CPU real-time factor is part of that answer.
"""
import os
import sys

sys.path.insert(0, "/app/common")
from runner import run_all  # noqa: E402

EMBEDDINGS = {
    "campplus": "/models/3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx",
    "resnet34": "/models/wespeaker_en_voxceleb_resnet34_LM.onnx",
    "titanet": "/models/nemo_en_titanet_large.onnx",
}


def load():
    import sherpa_onnx

    provider, threads = os.getenv("PROVIDER", "cpu"), int(os.getenv("THREADS", "16"))
    config = sherpa_onnx.OfflineSpeakerDiarizationConfig(
        segmentation=sherpa_onnx.OfflineSpeakerSegmentationModelConfig(
            pyannote=sherpa_onnx.OfflineSpeakerSegmentationPyannoteModelConfig(
                model="/models/sherpa-onnx-pyannote-segmentation-3-0/model.onnx"),
            num_threads=threads, provider=provider),
        embedding=sherpa_onnx.SpeakerEmbeddingExtractorConfig(
            model=EMBEDDINGS[os.getenv("EMBED", "campplus")], num_threads=threads, provider=provider),
        clustering=sherpa_onnx.FastClusteringConfig(num_clusters=-1, threshold=float(os.getenv("THRESHOLD", "0.5"))),
        min_duration_on=0.3,
        min_duration_off=0.5,
    )
    if not config.validate():
        raise RuntimeError("sherpa-onnx config invalid - a model file is missing")
    sd = sherpa_onnx.OfflineSpeakerDiarization(config)

    def diarize(wav, sr):
        assert sr == sd.sample_rate, f"expected {sd.sample_rate} Hz, got {sr}"
        result = sd.process(wav).sort_by_start_time()
        return {"": [(r.start, r.end, f"spk{r.speaker:02d}") for r in result]}

    return diarize


if __name__ == "__main__":
    run_all(os.environ.get("NAME", f"sherpa_{os.getenv('EMBED', 'campplus')}"), load, only=sys.argv[1:] or None)
