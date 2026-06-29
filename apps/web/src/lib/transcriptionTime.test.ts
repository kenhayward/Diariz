import { describe, it, expect } from "vitest";
import { transcriptionTimeParts } from "./transcriptionTime";

describe("transcriptionTimeParts", () => {
  it("formats under a minute with no day part and no zero-prefixed leading unit", () => {
    expect(transcriptionTimeParts(42_000)).toEqual({ days: 0, clock: "0:42" });
  });

  it("formats hours as h:mm:ss", () => {
    expect(transcriptionTimeParts(3_661_000)).toEqual({ days: 0, clock: "1:01:01" });
  });

  it("splits whole days off the clock", () => {
    // 1 day + 1:01:01
    expect(transcriptionTimeParts(90_061_000)).toEqual({ days: 1, clock: "1:01:01" });
  });

  it("handles multiple whole days with a zero remainder", () => {
    expect(transcriptionTimeParts(2 * 86_400_000)).toEqual({ days: 2, clock: "0:00" });
  });

  it("clamps negatives/zero to 0:00", () => {
    expect(transcriptionTimeParts(0)).toEqual({ days: 0, clock: "0:00" });
    expect(transcriptionTimeParts(-5)).toEqual({ days: 0, clock: "0:00" });
  });
});
