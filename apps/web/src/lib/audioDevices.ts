// Pure helpers for the recorder's audio-source dropdown and capture-constraint toggles.
// Kept free of React and browser APIs so the ordering/fallback/mapping logic is unit-testable
// (mirrors segmentPlayback.ts / actionsView.ts). The component in Recorder.tsx does the actual
// enumeration/getUserMedia and localStorage IO; this module just shapes and reconciles the data.

export type SourceKind = "default" | "device" | "system";

export interface SourceSelection {
  kind: SourceKind;
  /** Present only for kind === "device". */
  deviceId?: string;
  /** The device's label at the time it was chosen (used for id-rotation fallback). */
  label?: string;
}

/** A microphone input from `enumerateDevices()` (audioinput kind only). */
export interface InputDevice {
  deviceId: string;
  label: string;
}

/** A single `<option>` for the source dropdown. `token` is the persisted/encoded value. */
export interface SourceOption {
  token: string;
  label: string;
  kind: SourceKind;
}

/** Translated strings the (otherwise pure) option builder needs. */
export interface SourceOptionLabels {
  micDefault: string;
  system: string;
  numbered: (n: number) => string;
}

/** What we persist to localStorage under `diariz.recorder.source`. */
export interface PersistedSource {
  token: string;
  label?: string;
}

/** The capture-constraint toggles persisted under `diariz.recorder.audioConstraints`. */
export interface AudioConstraints {
  echoCancellation: boolean;
  noiseSuppression: boolean;
  autoGainControl: boolean;
  mono: boolean;
}

// Defaults deliberately mirror today's `getUserMedia({ audio: true })` behaviour (browser DSP on),
// plus mono — diarization/ASR are mono and it keeps uploads smaller.
export const DEFAULT_CONSTRAINTS: AudioConstraints = {
  echoCancellation: true,
  noiseSuppression: true,
  autoGainControl: true,
  mono: true,
};

const DEVICE_PREFIX = "dev:";

// Windows/Chromium exposes two synthetic input entries that are aliases of a real device — deviceId
// "default" ("Default - …") and "communications" ("Communications - …"). We already offer our own
// "Microphone (default)" (OS default) at the top, so listing these would just duplicate the physical
// mic under confusing names. Drop them and keep the real devices.
const ALIAS_DEVICE_IDS = new Set(["default", "communications"]);

// Trailing USB vendor:product code some browsers append, e.g. "Microphone (Yeti Nano) (046d:0ab1)".
const HARDWARE_ID_SUFFIX = /\s*\([0-9a-f]{4}:[0-9a-f]{4}\)\s*$/i;

/** Strip the trailing `(vvvv:pppp)` hardware id from a device label; leave other parentheticals alone. */
export function cleanDeviceLabel(label: string): string {
  return label.replace(HARDWARE_ID_SUFFIX, "");
}

/** Drop the Windows default/communications alias entries and clean each remaining device's label. */
export function normalizeInputDevices(devices: InputDevice[]): InputDevice[] {
  return devices
    .filter((d) => !ALIAS_DEVICE_IDS.has(d.deviceId))
    .map((d) => ({ deviceId: d.deviceId, label: cleanDeviceLabel(d.label) }));
}

/** Decode a dropdown/persisted token into a selection. Unknown/blank → default (never throws). */
export function parseSourceToken(value: string): SourceSelection {
  if (value === "system") return { kind: "system" };
  if (value.startsWith(DEVICE_PREFIX)) {
    const deviceId = value.slice(DEVICE_PREFIX.length);
    if (deviceId) return { kind: "device", deviceId };
  }
  return { kind: "default" };
}

/** Encode a selection back into its token. */
export function formatSourceToken(sel: SourceSelection): string {
  if (sel.kind === "system") return "system";
  if (sel.kind === "device" && sel.deviceId) return `${DEVICE_PREFIX}${sel.deviceId}`;
  return "default";
}

/**
 * Ordered dropdown options: Microphone (default) first, one entry per enumerated device, System last.
 * A device shows its real label when `hasLabels` and the label is non-empty; otherwise a generic
 * "Microphone N" (browsers withhold labels until a getUserMedia grant).
 */
export function buildSourceOptions(
  devices: InputDevice[],
  hasLabels: boolean,
  labels: SourceOptionLabels,
): SourceOption[] {
  const options: SourceOption[] = [
    { token: "default", label: labels.micDefault, kind: "default" },
  ];
  devices.forEach((d, i) => {
    const label = hasLabels && d.label ? d.label : labels.numbered(i + 1);
    options.push({ token: `${DEVICE_PREFIX}${d.deviceId}`, label, kind: "device" });
  });
  options.push({ token: "system", label: labels.system, kind: "system" });
  return options;
}

/**
 * Reconcile a persisted source against the currently-available devices. Device ids rotate when the
 * user clears site data, so a saved `dev:<id>` that's gone falls back to a label match, then to the
 * default. Never leaves a dead selection pointing at a missing device.
 */
export function resolvePersistedSource(
  saved: PersistedSource | null | undefined,
  devices: InputDevice[],
): SourceSelection {
  if (!saved) return { kind: "default" };
  const sel = parseSourceToken(saved.token);
  if (sel.kind !== "device") return sel;

  if (sel.deviceId && devices.some((d) => d.deviceId === sel.deviceId)) {
    return { kind: "device", deviceId: sel.deviceId, label: saved.label };
  }
  if (saved.label) {
    const byLabel = devices.find((d) => d.label && d.label === saved.label);
    if (byLabel) return { kind: "device", deviceId: byLabel.deviceId, label: byLabel.label };
  }
  return { kind: "default" };
}

/** Map the toggle state to a MediaTrackConstraints fragment for getUserMedia's `audio` object. */
export function toMediaTrackConstraints(c: AudioConstraints): MediaTrackConstraints {
  return {
    echoCancellation: c.echoCancellation,
    noiseSuppression: c.noiseSuppression,
    autoGainControl: c.autoGainControl,
    channelCount: c.mono ? 1 : 2,
  };
}
