import { useEffect, useRef } from "react";
import { rms, normalizeLevel, nextSilenceMs, SILENCE_HINT_MS } from "../../lib/audioLevel";

const BARS = 5;
// Lift the (normalised 0..1) per-band magnitude into a lively bar height, and never let a bar fully
// collapse so the meter still reads as "live" during quiet passages.
const BAR_GAIN = 1.9;
const BAR_FLOOR = 0.08;

// A live 5-bar input-level meter for the recording hero. It taps the recording MediaStream via a passive
// Web Audio AnalyserNode (a read only - NOT connected to the output, so nothing is echoed to the speakers).
// The frequency spectrum is split low→high into 5 bands; each band's average magnitude drives one bar's
// scaleY (written straight to the DOM via refs, so the parent never re-renders at 60fps). Overall RMS from
// the time-domain buffer still feeds the debounced silence hint via onSilentChange (same semantics as the
// old single-bar InputLevelMeter it supersedes).
export default function HubLevelMeter({
  stream,
  onSilentChange,
}: {
  stream: MediaStream | null;
  onSilentChange?: (silent: boolean) => void;
}) {
  const barRefs = useRef<(HTMLDivElement | null)[]>([]);

  useEffect(() => {
    if (!stream) return;
    const Ctx =
      (window as unknown as { AudioContext?: typeof AudioContext; webkitAudioContext?: typeof AudioContext })
        .AudioContext ??
      (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctx) return;

    const ctx = new Ctx();
    const source = ctx.createMediaStreamSource(stream);
    const analyser = ctx.createAnalyser();
    analyser.fftSize = 256;
    analyser.smoothingTimeConstant = 0.6;
    source.connect(analyser); // tap only — do NOT connect to ctx.destination

    const freq = new Uint8Array(analyser.frequencyBinCount);
    const time = new Uint8Array(analyser.fftSize);
    const groupSize = Math.max(1, Math.floor(freq.length / BARS));
    let raf = 0;
    let last: number | null = null;
    let silentMs = 0;
    let silent = false;

    const tick = (now: number) => {
      // Per-bar heights from the frequency spectrum, low band → high band.
      analyser.getByteFrequencyData(freq);
      for (let b = 0; b < BARS; b++) {
        const start = b * groupSize;
        const end = b === BARS - 1 ? freq.length : start + groupSize;
        let sum = 0;
        for (let i = start; i < end; i++) sum += freq[i];
        const avg = sum / Math.max(1, end - start) / 255; // 0..1
        const scale = Math.min(1, Math.max(BAR_FLOOR, avg * BAR_GAIN));
        const el = barRefs.current[b];
        if (el) el.style.transform = `scaleY(${scale})`;
      }

      // Overall level (time-domain RMS) drives the debounced silence hint.
      analyser.getByteTimeDomainData(time);
      const level = normalizeLevel(rms(time));
      const dt = last == null ? 0 : now - last;
      last = now;
      silentMs = nextSilenceMs(silentMs, level, dt);
      const nowSilent = silentMs >= SILENCE_HINT_MS;
      if (nowSilent !== silent) {
        silent = nowSilent;
        onSilentChange?.(nowSilent);
      }
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);

    return () => {
      cancelAnimationFrame(raf);
      source.disconnect();
      void ctx.close();
      onSilentChange?.(false); // meter gone → no stale hint
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [stream]);

  if (!stream) return null;

  return (
    <div
      aria-hidden
      title="Input level"
      style={{ display: "flex", alignItems: "flex-end", gap: 2, height: 22 }}
    >
      {Array.from({ length: BARS }, (_, b) => (
        <div
          key={b}
          data-testid="hub-meter-bar"
          ref={(el) => {
            barRefs.current[b] = el;
          }}
          style={{
            width: 3,
            height: 22,
            borderRadius: 2,
            background: "var(--hub-meter-gradient)",
            transformOrigin: "bottom",
            transform: "scaleY(0.25)",
          }}
        />
      ))}
    </div>
  );
}
