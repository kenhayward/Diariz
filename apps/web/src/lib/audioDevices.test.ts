import { describe, it, expect } from "vitest";
import {
  parseSourceToken,
  formatSourceToken,
  buildSourceOptions,
  resolvePersistedSource,
  toMediaTrackConstraints,
  cleanDeviceLabel,
  normalizeInputDevices,
  DEFAULT_CONSTRAINTS,
  type InputDevice,
  type SourceOptionLabels,
} from "./audioDevices";

const labels: SourceOptionLabels = {
  micDefault: "Microphone (default)",
  noMic: "No microphone",
  numbered: (n) => `Microphone ${n}`,
};

const devices: InputDevice[] = [
  { deviceId: "aaa", label: "Built-in Mic" },
  { deviceId: "bbb", label: "USB Headset" },
];

describe("parseSourceToken / formatSourceToken", () => {
  it("round-trips the default token", () => {
    expect(parseSourceToken("default")).toEqual({ kind: "default" });
    expect(formatSourceToken({ kind: "default" })).toBe("default");
  });

  it("round-trips the system token", () => {
    expect(parseSourceToken("system")).toEqual({ kind: "system" });
    expect(formatSourceToken({ kind: "system" })).toBe("system");
  });

  it("round-trips the no-microphone token", () => {
    expect(parseSourceToken("none")).toEqual({ kind: "none" });
    expect(formatSourceToken({ kind: "none" })).toBe("none");
  });

  it("round-trips a device token, preserving the id", () => {
    expect(parseSourceToken("dev:aaa")).toEqual({ kind: "device", deviceId: "aaa" });
    expect(formatSourceToken({ kind: "device", deviceId: "aaa" })).toBe("dev:aaa");
  });

  it("treats an unknown/empty token as the default (never throws)", () => {
    expect(parseSourceToken("")).toEqual({ kind: "default" });
    expect(parseSourceToken("garbage")).toEqual({ kind: "default" });
  });
});

describe("buildSourceOptions", () => {
  it("orders default first, specifics in the middle, No Microphone last (system available)", () => {
    expect(buildSourceOptions(devices, true, labels, { canSystemAudio: true })).toEqual([
      { token: "default", label: "Microphone (default)", kind: "default" },
      { token: "dev:aaa", label: "Built-in Mic", kind: "device" },
      { token: "dev:bbb", label: "USB Headset", kind: "device" },
      { token: "none", label: "No microphone", kind: "none" },
    ]);
  });

  it("numbers devices generically when labels are withheld", () => {
    const withheld: InputDevice[] = [
      { deviceId: "aaa", label: "" },
      { deviceId: "bbb", label: "" },
    ];
    expect(buildSourceOptions(withheld, false, labels, { canSystemAudio: true })).toEqual([
      { token: "default", label: "Microphone (default)", kind: "default" },
      { token: "dev:aaa", label: "Microphone 1", kind: "device" },
      { token: "dev:bbb", label: "Microphone 2", kind: "device" },
      { token: "none", label: "No microphone", kind: "none" },
    ]);
  });

  it("falls back to a numbered label for an individual blank-label device", () => {
    const mixed: InputDevice[] = [
      { deviceId: "aaa", label: "Built-in Mic" },
      { deviceId: "bbb", label: "" },
    ];
    const opts = buildSourceOptions(mixed, true, labels, { canSystemAudio: true });
    expect(opts[1].label).toBe("Built-in Mic");
    expect(opts[2].label).toBe("Microphone 2");
  });

  it("omits No Microphone when system audio is unavailable (else Record could never enable)", () => {
    expect(buildSourceOptions([], false, labels, { canSystemAudio: false }).map((o) => o.token)).toEqual([
      "default",
    ]);
  });

  it("yields default + No Microphone when there are no devices and system is available", () => {
    expect(buildSourceOptions([], false, labels, { canSystemAudio: true }).map((o) => o.token)).toEqual([
      "default",
      "none",
    ]);
  });
});

describe("resolvePersistedSource (fallback ladder)", () => {
  it("returns default when nothing is saved", () => {
    expect(resolvePersistedSource(null, devices)).toEqual({ kind: "default" });
  });

  it("keeps default / system selections as-is", () => {
    expect(resolvePersistedSource({ token: "default" }, devices)).toEqual({ kind: "default" });
    expect(resolvePersistedSource({ token: "system" }, devices)).toEqual({ kind: "system" });
  });

  it("restores a device whose id still exists", () => {
    expect(resolvePersistedSource({ token: "dev:bbb", label: "USB Headset" }, devices)).toEqual({
      kind: "device",
      deviceId: "bbb",
      label: "USB Headset",
    });
  });

  it("matches by label when the stored id has rotated away", () => {
    const rotated: InputDevice[] = [{ deviceId: "zzz", label: "USB Headset" }];
    expect(resolvePersistedSource({ token: "dev:bbb", label: "USB Headset" }, rotated)).toEqual({
      kind: "device",
      deviceId: "zzz",
      label: "USB Headset",
    });
  });

  it("falls back to default when neither id nor label matches", () => {
    expect(resolvePersistedSource({ token: "dev:ccc", label: "Gone Mic" }, devices)).toEqual({
      kind: "default",
    });
  });
});

describe("cleanDeviceLabel", () => {
  it("strips a trailing USB vendor:product hardware code", () => {
    expect(cleanDeviceLabel("Microphone (Yeti Nano) (046d:0ab1)")).toBe("Microphone (Yeti Nano)");
  });

  it("leaves labels without a hardware code untouched (incl. non-hex parentheticals)", () => {
    expect(cleanDeviceLabel("Stereo Mix (Realtek(R) Audio)")).toBe("Stereo Mix (Realtek(R) Audio)");
    expect(cleanDeviceLabel("Built-in Mic")).toBe("Built-in Mic");
  });
});

describe("normalizeInputDevices", () => {
  it("drops the Windows default/communications aliases and strips hardware codes", () => {
    const raw: InputDevice[] = [
      { deviceId: "default", label: "Default - Microphone (Yeti Nano) (046d:0ab1)" },
      { deviceId: "communications", label: "Communications - Microphone (Yeti Nano) (046d:0ab1)" },
      { deviceId: "abc123", label: "Microphone (Yeti Nano) (046d:0ab1)" },
      { deviceId: "def456", label: "Stereo Mix (Realtek(R) Audio)" },
    ];
    expect(normalizeInputDevices(raw)).toEqual([
      { deviceId: "abc123", label: "Microphone (Yeti Nano)" },
      { deviceId: "def456", label: "Stereo Mix (Realtek(R) Audio)" },
    ]);
  });

  it("keeps a device list that has no aliases unchanged (labels cleaned)", () => {
    expect(normalizeInputDevices([{ deviceId: "x", label: "USB Mic (1234:abcd)" }])).toEqual([
      { deviceId: "x", label: "USB Mic" },
    ]);
  });
});

describe("toMediaTrackConstraints", () => {
  it("maps the defaults (all DSP on, mono)", () => {
    expect(DEFAULT_CONSTRAINTS).toEqual({
      echoCancellation: true,
      noiseSuppression: true,
      autoGainControl: true,
      mono: true,
    });
    expect(toMediaTrackConstraints(DEFAULT_CONSTRAINTS)).toEqual({
      echoCancellation: true,
      noiseSuppression: true,
      autoGainControl: true,
      channelCount: 1,
    });
  });

  it("maps DSP off + stereo", () => {
    expect(
      toMediaTrackConstraints({
        echoCancellation: false,
        noiseSuppression: false,
        autoGainControl: false,
        mono: false,
      }),
    ).toEqual({
      echoCancellation: false,
      noiseSuppression: false,
      autoGainControl: false,
      channelCount: 2,
    });
  });
});
