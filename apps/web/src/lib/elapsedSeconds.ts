import { useEffect, useState } from "react";

/// Whole seconds elapsed since `active` last became true; 0 whenever it is false.
///
/// Used to put a visible count-up on an in-flight operation that has no progress of its own to report
/// (the recorder's upload), so a slow one reads as working rather than frozen.
///
/// Elapsed is measured against the wall clock rather than counted per tick: a browser throttles timers in a
/// background tab, so a tick count would under-report exactly when the user has looked away and is most
/// likely to come back suspecting a hang.
export function useElapsedSeconds(active: boolean): number {
  const [seconds, setSeconds] = useState(0);

  useEffect(() => {
    if (!active) {
      setSeconds(0);
      return;
    }
    const startedAt = Date.now();
    setSeconds(0);
    const id = window.setInterval(() => setSeconds(Math.floor((Date.now() - startedAt) / 1000)), 1000);
    return () => window.clearInterval(id);
  }, [active]);

  return seconds;
}
