"""Grid-search Nemotron's post-processing on saved frame probabilities (run.py with SAVE_PROBS=1).

  SET=ami-dev python nemotron/tune_postproc.py probs_nemotron_offline_default

Post-processing turns per-speaker frame probabilities into segments: onset/offset hysteresis thresholds,
padding, and minimum speech/silence durations. It is pure arithmetic on the probabilities, so the model
runs once and every setting is replayed through NeMo's own `predlist_to_timestamps`, exactly as
`diarize()` would apply it. Tune on AMI **dev**, then score the winner once on test and on our set.

Prints the top settings by DER (collar 0, like the AMI table) and writes the best to
/data/out/<SET>/postproc_best.yaml in NeMo's post-processing format.
"""
import itertools
import json
import os
import sys
from pathlib import Path

import numpy as np
import torch
from omegaconf import OmegaConf
from pyannote.core import Annotation, Segment, Timeline
from pyannote.metrics.diarization import DiarizationErrorRate

from nemo.collections.asr.parts.utils.vad_utils import predlist_to_timestamps

GRID = {
    "onset": [0.3, 0.4, 0.5, 0.6],
    "offset": [0.3, 0.5, 0.7],
    "pad_onset": [0.0, 0.1, 0.2],
    "pad_offset": [0.0, 0.1, 0.2],
    "min_duration_on": [0.0],
    "min_duration_off": [0.0, 0.15, 0.3],
}


def rttm(path, uri):
    a = Annotation(uri=uri)
    for i, line in enumerate(path.read_text().splitlines()):
        p = line.split()
        if len(p) > 7 and p[0] == "SPEAKER":
            a[Segment(float(p[3]), float(p[3]) + float(p[4])), i] = p[7]
    return a


def main():
    root = Path("/data") / os.getenv("SET", "ami-dev")
    probs_dir = Path("/data/out") / os.getenv("SET", "ami-dev") / sys.argv[1]
    files = sorted(probs_dir.glob("*.npy"))
    data = []
    for f in files:
        uri = f.stem
        _, _, s, e = (root / "uem" / f"{uri}.uem").read_text().split()[:4]
        probs = np.load(f)
        # Frame length in 10 ms units, from the file itself: diarize() emits 10 ms frames for this model
        # (output_subsampling_factor 1), not the 80 ms its streaming settings are expressed in.
        unit = max(1, round(float(e) / (probs.shape[0] * 0.01)))
        data.append((uri, torch.from_numpy(probs).unsqueeze(0), rttm(root / "ref" / f"{uri}.rttm", uri),
                     Timeline([Segment(float(s), float(e))]), float(e), unit))
    print(f"{len(data)} files, {np.prod([len(v) for v in GRID.values()])} settings", flush=True)

    results = []
    for values in itertools.product(*GRID.values()):
        params = dict(zip(GRID.keys(), values))
        der = DiarizationErrorRate(collar=0.0)
        for uri, preds, ref, uem, dur, unit in data:
            stamps = predlist_to_timestamps(
                batch_preds_list=[preds], audio_rttm_map_dict={uri: {"offset": 0.0, "duration": dur}},
                cfg_vad_params=OmegaConf.create(dict(params)), unit_10ms_frame_count=unit)[0]
            hyp = Annotation(uri=uri)
            for spk, segs in enumerate(stamps):
                for k, (a, b) in enumerate(segs):
                    if b > a:
                        hyp[Segment(float(a), float(b)), f"{spk}-{k}"] = f"spk{spk}"
            der(ref, hyp, uem=uem)
        d = der[:]
        tot = d["total"]
        results.append({**params, "der": abs(der), "missed": d["missed detection"] / tot,
                        "fa": d["false alarm"] / tot, "confusion": d["confusion"] / tot})

    results.sort(key=lambda r: r["der"])
    for r in results[:10]:
        print(json.dumps({k: (round(v, 4) if isinstance(v, float) else v) for k, v in r.items()}))
    best = {k: results[0][k] for k in GRID}
    out = Path("/data/out") / os.getenv("SET", "ami-dev") / "postproc_best.yaml"
    out.write_text(OmegaConf.to_yaml(OmegaConf.create({"parameters": best})))
    print(f"best written to {out}")


if __name__ == "__main__":
    main()
