"""Slice base.wav into the duration ladder used by the per-chunk floor measurement.

Pure stdlib (wave), so it runs anywhere without ffmpeg. Every clip is a contiguous window
taken from the same offset, so length is the only variable across the ladder - which is what
lets a linear fit separate the fixed per-job floor from the marginal cost per second.

    python slice.py                       # base.wav -> clips/
    python slice.py --src other.wav --out other-clips
"""
import argparse
import os
import wave

# Short end probes the fixed floor; the long end anchors the slope.
DURATIONS = [5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240]

# Start a little way in so every clip opens mid-speech rather than on the synthesiser's
# leading silence, which would otherwise make the shortest clips mostly quiet.
OFFSET_S = 2.0


def main() -> None:
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default=os.path.join(here, "base.wav"))
    ap.add_argument("--out", default=os.path.join(here, "clips"))
    args = ap.parse_args()

    with wave.open(args.src, "rb") as src:
        ch, width = src.getnchannels(), src.getsampwidth()
        rate, frames = src.getframerate(), src.getnframes()
        total_s = frames / rate
        print(f"source: {ch}ch {width * 8}bit {rate}Hz  {total_s:.1f}s  ({frames} frames)")
        src.setpos(int(OFFSET_S * rate))
        body = src.readframes(frames - int(OFFSET_S * rate))

    os.makedirs(args.out, exist_ok=True)
    written = 0
    for secs in DURATIONS:
        want = int(secs * rate) * ch * width
        if want > len(body):
            print(f"  skip {secs:>4}s - source is only {total_s - OFFSET_S:.1f}s after the offset")
            continue
        path = os.path.join(args.out, f"clip-{secs:03d}s.wav")
        with wave.open(path, "wb") as dst:
            dst.setnchannels(ch)
            dst.setsampwidth(width)
            dst.setframerate(rate)
            dst.writeframes(body[:want])
        print(f"  clip-{secs:03d}s.wav  {os.path.getsize(path) / 1024:8.1f} KiB")
        written += 1

    print(f"\n{written} clips in {args.out}")


if __name__ == "__main__":
    main()
