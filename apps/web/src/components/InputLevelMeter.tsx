import { useEffect, useRef } from "react";
import { rms, normalizeLevel, nextSilenceMs, SILENCE_HINT_MS } from "../lib/audioLevel";

// A live microphone input-level meter — a smooth gradient bar that fills green→amber→red with the
// received audio level. It taps the recording MediaStream via a Web Audio AnalyserNode (a passive
// read; NOT connected to the output, so nothing is echoed to the speakers). All per-frame work stays
// in here (bar written via a ref, no React state), so the parent doesn't re-render at 60fps; the only
// thing lifted out is the debounced `silent` boolean via onSilentChange.
export default function InputLevelMeter({
  stream,
  onSilentChange,
}: {
  stream: MediaStream | null;
  onSilentChange?: (silent: boolean) => void;
}) {
  const fillRef = useRef<HTMLDivElement>(null);

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

    const buf = new Uint8Array(analyser.fftSize);
    let raf = 0;
    let last: number | null = null;
    let silentMs = 0;
    let silent = false;

    const tick = (now: number) => {
      analyser.getByteTimeDomainData(buf);
      const level = normalizeLevel(rms(buf));
      if (fillRef.current) fillRef.current.style.transform = `scaleX(${level})`;

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
      className="h-1.5 w-20 overflow-hidden rounded-full bg-gray-200 dark:bg-gray-700"
    >
      <div
        ref={fillRef}
        className="h-full w-full origin-left rounded-full bg-gradient-to-r from-green-500 via-amber-400 to-red-500"
        style={{ transform: "scaleX(0)" }}
      />
    </div>
  );
}
