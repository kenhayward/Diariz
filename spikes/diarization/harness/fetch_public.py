"""Fetch the public AMI test set into /data/ami, in the same layout as our own set (audio/ref/uem/manifest).

  python fetch_public.py

Audio: the 16 AMI test meetings, Mix-Headset (all headsets summed - the closest AMI condition to a
recorded call), about 9 hours and 1 GB, from the AMI corpus mirror (CC-BY-4.0).
Reference: pyannote's AMI-diarization-setup "only_words" RTTMs and UEMs - the standard protocol the
published AMI-IHM numbers use, so ours can be compared with them.
"""
from __future__ import annotations

import json
import subprocess
import urllib.error
import urllib.request
from pathlib import Path

SETUP = "https://raw.githubusercontent.com/pyannote/AMI-diarization-setup/main"
AUDIO = "https://groups.inf.ed.ac.uk/ami/AMICorpusMirror/amicorpus/{m}/audio/{m}.Mix-Headset.wav"


def get(url: str) -> bytes:
    with urllib.request.urlopen(url, timeout=120) as r:
        return r.read()


def main(out: Path = Path("/data/ami")):
    for sub in ("audio", "ref", "uem", "_raw"):
        (out / sub).mkdir(parents=True, exist_ok=True)
    meetings = get(f"{SETUP}/lists/test.meetings.txt").decode().split()
    manifest = {}
    for m in meetings:
        (out / "ref" / f"{m}.rttm").write_bytes(get(f"{SETUP}/only_words/rttms/test/{m}.rttm"))
        (out / "uem" / f"{m}.uem").write_bytes(get(f"{SETUP}/uems/test/{m}.uem"))
        wav = out / "audio" / f"{m}.wav"
        if not wav.exists():
            raw = out / "_raw" / f"{m}.wav"
            for attempt in range(5):  # the mirror drops long transfers now and then
                try:
                    urllib.request.urlretrieve(AUDIO.format(m=m), raw)
                    break
                except urllib.error.ContentTooShortError:
                    print(f"{m}: truncated download, retry {attempt + 1}", flush=True)
            else:
                raise RuntimeError(f"{m}: download kept failing")
            subprocess.run(["ffmpeg", "-nostdin", "-loglevel", "error", "-y", "-i", str(raw),
                            "-ac", "1", "-ar", "16000", str(wav)], check=True)
            raw.unlink()
        speakers = {line.split()[7] for line in (out / "ref" / f"{m}.rttm").read_text().splitlines() if line}
        minutes = float((out / "uem" / f"{m}.uem").read_text().split()[3]) / 60
        manifest[m] = {"bucket": "3-4" if len(speakers) <= 4 else "5-8", "source": "ami",
                       "minutes": round(minutes, 1), "ref_speakers": len(speakers)}
        print(m, manifest[m], flush=True)
    (out / "_raw").rmdir()
    (out / "manifest.json").write_text(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
