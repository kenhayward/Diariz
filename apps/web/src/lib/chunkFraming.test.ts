import { describe, expect, it, vi } from "vitest";
import { probeFraming, resetFramingProbe } from "./chunkFraming";

const frag = (body: string) => new Blob([body], { type: "audio/webm" });

describe("probeFraming", () => {
  it("reports fragments when a byte-joined pair decodes", async () => {
    const decode = vi.fn().mockResolvedValue(undefined);
    resetFramingProbe();

    const mode = await probeFraming({
      recordFragments: async () => [frag("header+a"), frag("b")],
      decode,
    });

    expect(mode).toBe("fragments");
    // It must decode the JOIN, not a fragment on its own - a lone fragment fails everywhere, so a
    // probe that tested one would always answer "restart".
    const decoded = decode.mock.calls[0][0] as Blob;
    expect(decoded.size).toBe(frag("header+a").size + frag("b").size);
  });

  it("falls back to restart when the joined pair will not decode", async () => {
    // Firefox and Safari were never covered by the S0 spike (docs section 5.1 findings), so this is
    // the path an unverified browser takes.
    resetFramingProbe();

    const mode = await probeFraming({
      recordFragments: async () => [frag("a"), frag("b")],
      decode: async () => {
        throw new Error("EBML header parsing failed");
      },
    });

    expect(mode).toBe("restart");
  });

  it("falls back to restart when the recorder cannot produce two fragments", async () => {
    // A browser that ignores the timeslice hands back one blob. There is nothing to join, so there
    // is nothing to trust.
    resetFramingProbe();

    expect(
      await probeFraming({ recordFragments: async () => [frag("only")], decode: async () => {} }),
    ).toBe("restart");
  });

  it("falls back to restart rather than rejecting when recording itself throws", async () => {
    // An unknown browser must degrade, never lose the recording. A rejection here would propagate
    // into start() and stop the capture before it began.
    resetFramingProbe();

    expect(
      await probeFraming({
        recordFragments: async () => {
          throw new Error("MediaRecorder unsupported");
        },
        decode: async () => {},
      }),
    ).toBe("restart");
  });

  it("probes once per session and reuses the answer", async () => {
    // The probe costs about a second of recording. Running it per chunk would be absurd.
    const recordFragments = vi.fn().mockResolvedValue([frag("a"), frag("b")]);
    resetFramingProbe();

    const first = await probeFraming({ recordFragments, decode: async () => {} });
    const second = await probeFraming({ recordFragments, decode: async () => {} });

    expect(first).toBe("fragments");
    expect(second).toBe("fragments");
    expect(recordFragments).toHaveBeenCalledTimes(1);
  });

  it("does not run twice when two callers race it", async () => {
    let resolveRecord: (v: Blob[]) => void = () => {};
    const recordFragments = vi.fn().mockImplementation(
      () => new Promise<Blob[]>((r) => { resolveRecord = r; }),
    );
    resetFramingProbe();

    const a = probeFraming({ recordFragments, decode: async () => {} });
    const b = probeFraming({ recordFragments, decode: async () => {} });
    resolveRecord([frag("a"), frag("b")]);

    expect(await a).toBe("fragments");
    expect(await b).toBe("fragments");
    expect(recordFragments).toHaveBeenCalledTimes(1);
  });
});
