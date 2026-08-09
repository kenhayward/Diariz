import { useQuery } from "@tanstack/react-query";
import { api } from "./api";
import type { CalendarRecordingSettings } from "./calendarRecording";

/// Server defaults, mirrored so the recorder has something sane before settings load (or if they fail to).
/// Auto-stop off is the safe default in both directions: a recording that outlives its meeting is a nuisance,
/// one that ends mid-sentence is a lost meeting.
const FALLBACK: CalendarRecordingSettings = { enabled: false, afterMinutes: 3, silenceSeconds: 30 };

/**
 * The user's "Recording from a Calendar Event" preferences, for the recorder to apply to a take started by
 * joining a meeting.
 *
 * Its own hook rather than a field on `useRoom` (which already reads the same query for the placement
 * preference) because this has nothing to do with rooms, and rather than a `useQuery` inside `Recorder`
 * because the recorder is rendered without a QueryClient in its tests. The query key is shared, so this
 * costs no extra request.
 */
export function useCalendarRecordingSettings(): CalendarRecordingSettings {
  const { data } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });
  if (!data) return FALLBACK;
  return {
    enabled: data.calendarAutoStopEnabled ?? FALLBACK.enabled,
    afterMinutes: data.calendarAutoStopAfterMinutes ?? FALLBACK.afterMinutes,
    silenceSeconds: data.calendarSilenceStopSeconds ?? FALLBACK.silenceSeconds,
  };
}
