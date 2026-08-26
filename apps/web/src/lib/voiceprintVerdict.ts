import type { SampleDiagnosis } from "./types";

/// How a recording behind a voiceprint is described, and where it sorts.
///
/// Pure, and deliberately free of i18n and React: the flip from distance to similarity and the
/// ordering are the two things here most worth a test, and the two least worth a DOM to test them
/// through. The caller maps a `RowVerdict` to a locale key.

/// What a person's card says about one of the recordings behind their voiceprint.
///
/// `unlinked` is not a judgement about the voice - it means the speaker no longer names this person -
/// so it is kept separate from the three the server derives from distances.
export type RowVerdict = "impostor" | "alone" | "unlinked" | "variant" | "core" | "only";

/// A cosine distance as a percentage similarity.
///
/// The tab used to print the distance itself under a label that reads as a match, so the worst row on
/// the screen carried the largest and most reassuring-looking number. Clamped at zero because cosine
/// distance runs to 2 for opposed vectors, and nobody can act on "-13% similar".
export function similarityPercent(distance: number): number {
  return Math.max(0, Math.round((1 - distance) * 100));
}

export function rowVerdict(
  diagnosis: SampleDiagnosis | undefined,
  stillLinked: boolean,
): RowVerdict {
  // Checked before the diagnosis: a sample whose speaker has moved may still carry a verdict from
  // before it moved, and reporting that would send someone to fix the wrong thing.
  if (!stillLinked) return "unlinked";
  if (!diagnosis) return "only";

  switch (diagnosis.verdict) {
    case "Impostor":
      return "impostor";
    case "Alone":
      return "alone";
    case "Variant":
      return "variant";
    case "Core":
      return "core";
    default:
      return "only";
  }
}

/// Rows worth acting on first. Everything that needs no attention shares one key, so a stable sort
/// leaves the server's ordering (by recording name) intact underneath - ranking `Core` against
/// `Variant` would imply a difference the user is supposed to do something about.
export function sortKey(verdict: RowVerdict): number {
  // Impostor first: "this is somebody else" is a different order of problem from "this sounds unlike the
  // rest", and the only one that turns into a confident match for the wrong person if the set is clustered.
  if (verdict === "impostor") return 0;
  if (verdict === "alone") return 1;
  if (verdict === "unlinked") return 2;
  return 3;
}

/// Whether this row is one of the ones the "only ones worth checking" filter keeps.
export function worthChecking(verdict: RowVerdict): boolean {
  return verdict === "impostor" || verdict === "alone" || verdict === "unlinked";
}
