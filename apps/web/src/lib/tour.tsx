import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";
import { TOUR_STEPS, hasOnboarded, setOnboarded } from "./onboarding";

interface TourValue {
  active: boolean;
  index: number;
  steps: typeof TOUR_STEPS;
  start: () => void; // manual replay (account menu / "Take the tour")
  next: () => void; // advances; finishing the last step ends + marks onboarded
  back: () => void;
  end: () => void; // skip / close — also marks onboarded so it won't auto-show again
}

const noop = () => {};
const TourContext = createContext<TourValue>({
  active: false,
  index: 0,
  steps: TOUR_STEPS,
  start: noop,
  next: noop,
  back: noop,
  end: noop,
});

export function TourProvider({ children }: { children: ReactNode }) {
  const [active, setActive] = useState(false);
  const [index, setIndex] = useState(0);

  // Auto-start once per browser, after a tick so the target regions are mounted and measurable.
  useEffect(() => {
    if (hasOnboarded()) return;
    const t = window.setTimeout(() => {
      setIndex(0);
      setActive(true);
    }, 500);
    return () => window.clearTimeout(t);
  }, []);

  const finish = useCallback(() => {
    setActive(false);
    setOnboarded(true);
  }, []);

  const start = useCallback(() => {
    setIndex(0);
    setActive(true);
  }, []);

  const next = useCallback(() => {
    setIndex((i) => {
      if (i + 1 >= TOUR_STEPS.length) {
        finish();
        return i;
      }
      return i + 1;
    });
  }, [finish]);

  const back = useCallback(() => setIndex((i) => Math.max(0, i - 1)), []);

  return (
    <TourContext.Provider value={{ active, index, steps: TOUR_STEPS, start, next, back, end: finish }}>
      {children}
    </TourContext.Provider>
  );
}

// eslint-disable-next-line react-refresh/only-export-components
export function useTour(): TourValue {
  return useContext(TourContext);
}
