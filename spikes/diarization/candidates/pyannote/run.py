"""pyannote.audio 4 candidates: the production baseline (speaker-diarization-3.1) and community-1.

  PIPELINE=pyannote/speaker-diarization-3.1        NAME=pyannote-3.1
  PIPELINE=pyannote/speaker-diarization-community-1 NAME=pyannote-community-1

community-1 returns both a regular diarization (overlapping speech allowed) and an "exclusive" one (one
speaker at a time), which is the one that maps cleanly onto word-level speaker assignment - so both are
scored, as <NAME>-regular and <NAME>-exclusive.
"""
import os
import sys

sys.path.insert(0, "/app/common")
from runner import run_all  # noqa: E402


def load():
    import torch
    from pyannote.audio import Pipeline

    pipeline = Pipeline.from_pretrained(os.environ["PIPELINE"], token=os.environ["HF_TOKEN"])
    pipeline.to(torch.device("cuda"))

    def turns(annotation):
        return [(seg.start, seg.end, spk) for seg, _, spk in annotation.itertracks(yield_label=True)]

    def diarize(wav, sr):
        out = pipeline({"waveform": torch.from_numpy(wav).unsqueeze(0), "sample_rate": sr})
        # pyannote 4 returns a DiarizeOutput; a legacy pipeline may still hand back a bare Annotation.
        if hasattr(out, "speaker_diarization"):
            result = {"regular": turns(out.speaker_diarization)}
            if getattr(out, "exclusive_speaker_diarization", None) is not None:
                result["exclusive"] = turns(out.exclusive_speaker_diarization)
            return result
        return {"": turns(out)}

    return diarize


if __name__ == "__main__":
    run_all(os.environ["NAME"], load, only=sys.argv[1:] or None)
