"""NVIDIA Nemotron-3-Diarization (D2): end-to-end streaming Sortformer, up to 8 speakers, via NeMo.

  MODE=offline (default, 30.4 s latency) | low (1.04 s) | very_low (0.64 s) | ultra_low (0.32 s)
  POSTPROC=default | dihard3 | callhome | amidev how frame probabilities become segments (onset/offset thresholds,
                                          padding, minimum durations). NeMo's own default is untuned; the
                                          other two are NVIDIA's published settings, tuned on those dev sets.

  SAVE_PROBS=1                            also save the raw per-frame speaker probabilities to
                                          /data/out/<SET>/probs_<name>/<uri>.npy, for tune_postproc.py.

The settings are the model card's table (values in 80 ms frames). `offline` is the like-for-like
comparison with pyannote; the low-latency modes are what live transcription would use.
"""
import os
import sys
import tempfile
from pathlib import Path

import numpy as np

import soundfile as sf

sys.path.insert(0, "/app/common")
from runner import run_all  # noqa: E402

# spkcache_len, fifo_len, chunk_len, chunk_right_context, spkcache_update_period
MODES = {
    "offline": (264, 40, 340, 40, 300),
    "low": (264, 264, 9, 4, 222),
    "very_low": (264, 264, 6, 2, 222),
    "ultra_low": (264, 264, 3, 1, 222),
}

POSTPROC = {
    "default": None,
    "dihard3": "/app/postproc/diar_streaming_sortformer_4spk-v2_dihard3-dev.yaml",
    "callhome": "/app/postproc/diar_streaming_sortformer_4spk-v2_callhome-part1.yaml",
    "amidev": "/data/out/ami-dev/postproc_best.yaml",  # written by tune_postproc.py
}


def parse(segment):
    # NeMo yields "start end speaker_N" strings; tolerate a tuple/list of the same three fields.
    parts = segment.split() if isinstance(segment, str) else list(segment)
    return float(parts[0]), float(parts[1]), str(parts[2])


def load():
    from nemo.collections.asr.models import SortformerEncLabelModel

    model = SortformerEncLabelModel.from_pretrained("nvidia/Nemotron-3-Diarization")
    model.eval()
    m = model.sortformer_modules
    (m.spkcache_len, m.fifo_len, m.chunk_len, m.chunk_right_context,
     m.spkcache_update_period) = MODES[os.getenv("MODE", "offline")]
    model._check_streaming_parameters()
    post = POSTPROC[os.getenv("POSTPROC", "default")]
    save_probs = os.getenv("SAVE_PROBS") == "1"
    probs_dir = Path("/data/out") / os.getenv("SET", "eval") / f"probs_{_name()}"

    def diarize(wav, sr):
        # diarize() takes file paths, so hand it the already-decoded audio through a temp wav.
        with tempfile.NamedTemporaryFile(suffix=".wav", dir="/dev/shm") as f:
            sf.write(f.name, wav, sr)
            out = model.diarize(audio=[f.name], batch_size=1, verbose=False, postprocessing_yaml=post,
                                include_tensor_outputs=save_probs)
        if save_probs:
            lines, preds = out
            probs_dir.mkdir(parents=True, exist_ok=True)
            np.save(probs_dir / f"{_current['uri']}.npy", preds[0].squeeze(0).float().cpu().numpy())
            out = lines
        return {"": [parse(s) for s in out[0]]}

    return diarize


# run_all does not pass the uri to diarize(); the probability saver needs it for the file name.
_current = {"uri": "warmup"}


def _name():
    return os.environ.get("NAME", f"nemotron_{os.getenv('MODE', 'offline')}_{os.getenv('POSTPROC', 'default')}")


if __name__ == "__main__":
    run_all(_name(), load, only=sys.argv[1:] or None, on_file=lambda uri: _current.update(uri=uri))
