import { describe, expect, it } from "vitest";
import { api } from "./api";
import { GATED_API_METHODS, PERMISSION_GATED_SURFACES, gatedMethodsUsedIn } from "./gatedApi";

/// The permission-boundary guard.
///
/// A page that quietly calls a permission-gated endpoint does not throw - it gets a 403 that renders as an
/// empty list, which looks identical to "nothing to show". That is exactly how the speaker picker shipped
/// broken for every user without ManagePeople. Component tests cannot catch it, because they mock the api
/// module and never cross a real boundary, so this scans source instead.
describe("permission boundaries", () => {
  const sources = import.meta.glob("../**/*.{ts,tsx}", { eager: true, query: "?raw", import: "default" }) as Record<
    string,
    string
  >;

  /// A typo'd or renamed key would silently stop guarding anything.
  it("names only methods that exist on the api client", () => {
    const missing = Object.keys(GATED_API_METHODS).filter(
      (m) => typeof (api as unknown as Record<string, unknown>)[m] !== "function",
    );

    expect(missing, "gated method names must match the api client").toEqual([]);
  });

  it("allows a gated call only from a surface declared administrative", () => {
    const offenders: string[] = [];

    for (const [path, src] of Object.entries(sources)) {
      const rel = path.replace(/^\.\.\//, "");
      if (rel.includes(".test.") || rel === "lib/gatedApi.ts" || rel === "lib/api.ts") continue;
      if (PERMISSION_GATED_SURFACES.some((surface) => rel.endsWith(surface))) continue;

      for (const method of gatedMethodsUsedIn(src)) {
        offenders.push(`${rel} calls api.${method} (needs ${GATED_API_METHODS[method]})`);
      }
    }

    expect(
      offenders,
      "A surface every signed-in user can reach must not call a permission-gated endpoint: the 403 renders " +
        "as an empty result rather than an error. Either use an ungated endpoint (searchPeople rather than " +
        "listPeople), or add the file to PERMISSION_GATED_SURFACES if it really is administrative.",
    ).toEqual([]);
  });

  /// The specific regression, pinned. The recording page must stay usable by someone with no platform
  /// permissions at all, because naming a speaker in your own meeting is not an administrative act.
  it("keeps the recording page free of gated calls", () => {
    const [, recordingDetail] =
      Object.entries(sources).find(([path]) => path.endsWith("pages/RecordingDetail.tsx")) ?? [];

    expect(recordingDetail, "RecordingDetail.tsx should be scannable").toBeTruthy();
    expect(gatedMethodsUsedIn(recordingDetail ?? "")).toEqual([]);
  });

  it("does not list a surface that no longer exists", () => {
    const known = Object.keys(sources).map((p) => p.replace(/^\.\.\//, ""));
    const stale = PERMISSION_GATED_SURFACES.filter((s) => !known.some((k) => k.endsWith(s)));

    expect(stale, "remove surfaces from PERMISSION_GATED_SURFACES once they are gone").toEqual([]);
  });
});
