import { describe, it, expect } from "vitest";
import {
  parseSourceToken,
  formatSourceToken,
  buildSourceOptions,
  resolvePersistedSource,
  toMediaTrackConstraints,
  DEFAULT_CONSTRAINTS,
  type InputDevice,
  type SourceOptionLabels,
} from "./audioDevices";

const labels: SourceOptionLabels = {
  micDefault: "Microphone (default)",
  system: "System audio",
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
  it("orders default first, specifics in the middle, system last (with labels)", () => {
    expect(buildSourceOptions(devices, true, labels)).toEqual([
      { token: "default", label: "Microphone (default)", kind: "default" },
      { token: "dev:aaa", label: "Built-in Mic", kind: "device" },
      { token: "dev:bbb", label: "USB Headset", kind: "device" },
      { token: "system", label: "System audio", kind: "system" },
    ]);
  });

  it("numbers devices generically when labels are withheld", () => {
    const withheld: InputDevice[] = [
      { deviceId: "aaa", label: "" },
      { deviceId: "bbb", label: "" },
    ];
    expect(buildSourceOptions(withheld, false, labels)).toEqual([
      { token: "default", label: "Microphone (default)", kind: "default" },
      { token: "dev:aaa", label: "Microphone 1", kind: "device" },
      { token: "dev:bbb", label: "Microphone 2", kind: "device" },
      { token: "system", label: "System audio", kind: "system" },
    ]);
  });

  it("falls back to a numbered label for an individual blank-label device", () => {
    const mixed: InputDevice[] = [
      { deviceId: "aaa", label: "Built-in Mic" },
      { deviceId: "bbb", label: "" },
    ];
    const opts = buildSourceOptions(mixed, true, labels);
    expect(opts[1].label).toBe("Built-in Mic");
    expect(opts[2].label).toBe("Microphone 2");
  });

  it("yields just default + system when there are no enumerated devices", () => {
    expect(buildSourceOptions([], false, labels).map((o) => o.token)).toEqual(["default", "system"]);
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
